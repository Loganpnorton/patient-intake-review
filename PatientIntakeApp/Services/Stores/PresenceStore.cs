using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Services.Stores;

public interface IPresenceStore
{
    Task HeartbeatAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<HashSet<Guid>> GetOnlineUserIdsAsync(TimeSpan onlineWindow, CancellationToken cancellationToken = default);
}

public class PresenceStore : IPresenceStore
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;

    public PresenceStore(IDbContextFactory<PatientIntakeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task HeartbeatAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) return;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;

        if (db.Database.IsSqlite())
        {
            // Fast upsert for SQLite.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO UserPresence (UserId, LastSeenAtUtc) VALUES ({0}, {1}) " +
                "ON CONFLICT(UserId) DO UPDATE SET LastSeenAtUtc = excluded.LastSeenAtUtc",
                userId,
                now);
            return;
        }

        var existing = await db.UserPresence.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (existing == null)
        {
            db.UserPresence.Add(new UserPresenceEntity { UserId = userId, LastSeenAtUtc = now });
        }
        else
        {
            existing.LastSeenAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<HashSet<Guid>> GetOnlineUserIdsAsync(TimeSpan onlineWindow, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - onlineWindow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var ids = await db.UserPresence
            .AsNoTracking()
            .Where(p => p.LastSeenAtUtc >= cutoff)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }
}

