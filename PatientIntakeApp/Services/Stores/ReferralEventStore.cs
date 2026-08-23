using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;
using System.Text.Json;

namespace PatientIntakeApp.Services.Stores;

public record TransferEventNotification(
    Guid EventId,
    Guid ReferralId,
    string SourceFileName,
    Guid? FromUserId,
    Guid ToUserId,
    DateTime AtUtc);

public interface IReferralEventStore
{
    Task<List<TransferEventNotification>> ListTransfersToUserSinceAsync(Guid toUserId, DateTime sinceUtc, CancellationToken cancellationToken = default);
}

public class ReferralEventStore : IReferralEventStore
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;

    public ReferralEventStore(IDbContextFactory<PatientIntakeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<TransferEventNotification>> ListTransfersToUserSinceAsync(Guid toUserId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        if (toUserId == Guid.Empty) return new List<TransferEventNotification>();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Narrow down server-side as much as we can, then parse payload JSON client-side.
        var needle = toUserId.ToString();

        var rows = await (
                from e in db.ReferralEvents.AsNoTracking()
                join r in db.Referrals.AsNoTracking() on e.ReferralId equals r.Id
                where e.Type == ReferralEventType.Transferred
                      && e.At > sinceUtc
                      && e.PayloadJson != null
                      && e.PayloadJson.Contains(needle)
                orderby e.At
                select new
                {
                    e.Id,
                    e.ReferralId,
                    r.SourceFileName,
                    e.At,
                    e.PayloadJson
                })
            .Take(50)
            .ToListAsync(cancellationToken);

        var result = new List<TransferEventNotification>();

        foreach (var row in rows)
        {
            try
            {
                using var doc = JsonDocument.Parse(row.PayloadJson!);
                var root = doc.RootElement;
                if (!root.TryGetProperty("toUserId", out var toEl)) continue;
                if (!Guid.TryParse(toEl.GetString(), out var parsedTo)) continue;
                if (parsedTo != toUserId) continue;

                Guid? from = null;
                if (root.TryGetProperty("fromUserId", out var fromEl) && Guid.TryParse(fromEl.GetString(), out var parsedFrom))
                {
                    from = parsedFrom;
                }

                result.Add(new TransferEventNotification(
                    row.Id,
                    row.ReferralId,
                    row.SourceFileName ?? string.Empty,
                    from,
                    parsedTo,
                    DateTime.SpecifyKind(row.At, DateTimeKind.Utc)));
            }
            catch
            {
                // ignore malformed payloads
            }
        }

        return result;
    }
}

