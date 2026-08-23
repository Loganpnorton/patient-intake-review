using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Services;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public class DatabaseInitializer : IDatabaseInitializer
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;
    private readonly IConfigurationService _configService;
    private readonly IPasswordHasher _passwordHasher;

    public DatabaseInitializer(
        IDbContextFactory<PatientIntakeDbContext> dbFactory,
        IConfigurationService configService,
        IPasswordHasher passwordHasher)
    {
        _dbFactory = dbFactory;
        _configService = configService;
        _passwordHasher = passwordHasher;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // SQLite local-dev: migrations were generated for SQL Server; use EnsureCreated for now.
        // SQL Server: apply migrations.
        if (db.Database.IsSqlite())
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            // Ensure presence table exists for existing sqlite db files (EnsureCreated won't evolve schema).
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS UserPresence (" +
                "UserId TEXT NOT NULL PRIMARY KEY, " +
                "LastSeenAtUtc TEXT NOT NULL" +
                ")",
                cancellationToken);
        }
        else
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        await SeedUsersAsync(db, cancellationToken);
        await SeedFacilitiesFromConfigAsync(db, cancellationToken);
        await SeedRulesFromConfigAsync(db, cancellationToken);
    }

    private async Task SeedUsersAsync(PatientIntakeDbContext db, CancellationToken cancellationToken)
    {
        // Portfolio builds do not ship a default password. Demo users are created
        // only when the developer explicitly opts in through a local environment variable.
        var demoPassword = Environment.GetEnvironmentVariable("PATIENTINTAKE_DEMO_PASSWORD");
        if (string.IsNullOrWhiteSpace(demoPassword)) return;

        var (hash, salt) = _passwordHasher.HashPassword(demoPassword);

        await UpsertDemoUserAsync(
            db,
            username: "demo-reviewer",
            displayName: "Demo Reviewer",
            role: (int)Models.UserRole.User,
            hash,
            salt,
            cancellationToken);

        await UpsertDemoUserAsync(
            db,
            username: "demo-admin",
            displayName: "Demo Administrator",
            role: (int)Models.UserRole.Developer,
            hash,
            salt,
            cancellationToken);
    }
    private static async Task UpsertDemoUserAsync(
        PatientIntakeDbContext db,
        string username,
        string displayName,
        int role,
        string hash,
        string salt,
        CancellationToken cancellationToken)
    {
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (existing == null)
        {
            db.Users.Add(new UserEntity
            {
                Username = username,
                DisplayName = displayName,
                PasswordHash = hash,
                PasswordSalt = salt,
                Role = role,
                IsActive = true
            });
            return;
        }

        existing.DisplayName = displayName;
        existing.PasswordHash = hash;
        existing.PasswordSalt = salt;
        existing.Role = role;
        existing.IsActive = true;
    }

    private async Task SeedFacilitiesFromConfigAsync(PatientIntakeDbContext db, CancellationToken cancellationToken)
    {
        var facilities = _configService.GetFacilities();
        if (facilities == null || facilities.Count == 0) return;

        // Track names assigned in this batch to prevent UNIQUE constraint violations
        // both against the database and within the batch itself.
        var batchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in facilities)
        {
            var legacyId = string.IsNullOrWhiteSpace(f.Id) ? null : f.Id.Trim();
            var name = string.IsNullOrWhiteSpace(f.Name) ? legacyId ?? "Facility" : f.Name.Trim();

            FacilityEntity? existing = null;
            if (!string.IsNullOrWhiteSpace(legacyId))
            {
                existing = await db.Facilities.FirstOrDefaultAsync(x => x.LegacyId == legacyId, cancellationToken);
                if (existing != null)
                {
                    batchNames.Add(existing.Name);
                }
            }
            existing ??= await db.Facilities.FirstOrDefaultAsync(x => x.Name == name, cancellationToken);

            if (existing == null)
            {
                if (batchNames.Contains(name))
                    continue; // Skip duplicate name within config batch

                batchNames.Add(name);
                db.Facilities.Add(new FacilityEntity { LegacyId = legacyId, Name = name });
            }
            else
            {
                existing.LegacyId ??= legacyId;

                // Only update name if it won't conflict with another facility.
                if (!string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    // Check if the target name is already taken by a different facility
                    // (either already persisted or assigned earlier in this batch).
                    var nameTaken = batchNames.Contains(name)
                        || await db.Facilities.AnyAsync(x => x.Name == name && x.Id != existing.Id, cancellationToken);

                    if (!nameTaken)
                    {
                        batchNames.Remove(existing.Name);
                        batchNames.Add(name);
                        existing.Name = name;
                    }
                }
                else
                {
                    batchNames.Add(name);
                }

                existing.UpdatedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedRulesFromConfigAsync(PatientIntakeDbContext db, CancellationToken cancellationToken)
    {
        var facilities = _configService.GetFacilities();
        if (facilities == null || facilities.Count == 0) return;

        foreach (var f in facilities)
        {
            var legacyId = string.IsNullOrWhiteSpace(f.Id) ? null : f.Id.Trim();
            if (string.IsNullOrWhiteSpace(legacyId)) continue;

            var facility = await db.Facilities.FirstOrDefaultAsync(x => x.LegacyId == legacyId, cancellationToken);
            if (facility == null) continue;

            var keywordRules = (f.Rules ?? new List<string>())
                .Select(r => (r ?? string.Empty).Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var rule in keywordRules)
            {
                var exists = await db.Rules.AnyAsync(r => r.FacilityId == facility.Id && r.Kind == Data.Entities.RuleKind.Keyword && r.Text == rule, cancellationToken);
                if (!exists)
                {
                    db.Rules.Add(new RuleEntity
                    {
                        FacilityId = facility.Id,
                        Kind = Data.Entities.RuleKind.Keyword,
                        Text = rule,
                        IsEnabled = true,
                        Severity = Data.Entities.RuleSeverity.Yellow,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
            }

            var contextRules = (f.ContextRules ?? new List<string>())
                .Select(r => (r ?? string.Empty).Trim())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var rule in contextRules)
            {
                var exists = await db.Rules.AnyAsync(r => r.FacilityId == facility.Id && r.Kind == Data.Entities.RuleKind.Context && r.Text == rule, cancellationToken);
                if (!exists)
                {
                    db.Rules.Add(new RuleEntity
                    {
                        FacilityId = facility.Id,
                        Kind = Data.Entities.RuleKind.Context,
                        Text = rule,
                        IsEnabled = true,
                        Severity = Data.Entities.RuleSeverity.Yellow,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}


