using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Services.Stores;

public interface IRuleStore
{
    Task<List<RuleEntity>> ListRulesAsync(string facilityLegacyId, RuleKind kind);
    Task<List<RuleEntity>> ListEnabledRulesAsync(string facilityLegacyId, RuleKind kind);
    Task<RuleEntity> AddRuleAsync(string facilityLegacyId, RuleKind kind, string text, bool isEnabled, RuleSeverity severity);
    Task UpdateRuleAsync(Guid ruleId, string text, bool isEnabled, RuleSeverity severity);
    Task DeleteRuleAsync(Guid ruleId);
}

public class RuleStore : IRuleStore
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;

    public RuleStore(IDbContextFactory<PatientIntakeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<RuleEntity>> ListRulesAsync(string facilityLegacyId, RuleKind kind)
    {
        if (string.IsNullOrWhiteSpace(facilityLegacyId)) return new List<RuleEntity>();
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Rules
            .Include(r => r.Facility)
            .AsNoTracking()
            .Where(r => r.Facility!.LegacyId == facilityLegacyId.Trim() && r.Kind == kind)
            .OrderBy(r => r.Text)
            .ToListAsync();
    }

    public async Task<List<RuleEntity>> ListEnabledRulesAsync(string facilityLegacyId, RuleKind kind)
    {
        if (string.IsNullOrWhiteSpace(facilityLegacyId)) return new List<RuleEntity>();
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Rules
            .Include(r => r.Facility)
            .AsNoTracking()
            .Where(r => r.Facility!.LegacyId == facilityLegacyId.Trim() && r.Kind == kind && r.IsEnabled)
            .OrderByDescending(r => r.Severity)
            .ThenBy(r => r.Text)
            .ToListAsync();
    }

    public async Task<RuleEntity> AddRuleAsync(string facilityLegacyId, RuleKind kind, string text, bool isEnabled, RuleSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(facilityLegacyId)) throw new ArgumentException("FacilityLegacyId required.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Rule text required.");

        var normalized = text.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var facility = await db.Facilities.FirstOrDefaultAsync(f => f.LegacyId == facilityLegacyId.Trim());
        if (facility == null)
        {
            facility = new FacilityEntity { LegacyId = facilityLegacyId.Trim(), Name = facilityLegacyId.Trim() };
            db.Facilities.Add(facility);
            await db.SaveChangesAsync();
        }

        // De-dupe by text within facility+kind (case-insensitive relies on SQL collation).
        var existing = await db.Rules.FirstOrDefaultAsync(r => r.FacilityId == facility.Id && r.Kind == kind && r.Text == normalized);
        if (existing != null)
        {
            existing.IsEnabled = isEnabled;
            existing.Severity = severity;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return existing;
        }

        var rule = new RuleEntity
        {
            FacilityId = facility.Id,
            Kind = kind,
            Text = normalized,
            IsEnabled = isEnabled,
            Severity = severity,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Rules.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    public async Task UpdateRuleAsync(Guid ruleId, string text, bool isEnabled, RuleSeverity severity)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == ruleId);
        if (rule == null) return;

        rule.Text = string.IsNullOrWhiteSpace(text) ? rule.Text : text.Trim();
        rule.IsEnabled = isEnabled;
        rule.Severity = severity;
        rule.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
    }

    public async Task DeleteRuleAsync(Guid ruleId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == ruleId);
        if (rule == null) return;
        db.Rules.Remove(rule);
        await db.SaveChangesAsync();
    }
}

