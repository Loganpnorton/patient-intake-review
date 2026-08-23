using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Services.ExternalChecks;

public interface IExternalCheckService
{
    Task RequestChecksAsync(Guid referralId, IEnumerable<ExternalCheckType> types, Guid? actorUserId);
}

public class ExternalCheckService : IExternalCheckService
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;
    private readonly IReadOnlyDictionary<ExternalCheckType, IExternalCheckProvider> _providers;

    public ExternalCheckService(IDbContextFactory<PatientIntakeDbContext> dbFactory, IEnumerable<IExternalCheckProvider> providers)
    {
        _dbFactory = dbFactory;
        _providers = (providers ?? Array.Empty<IExternalCheckProvider>()).ToDictionary(p => p.Type);
    }

    public async Task RequestChecksAsync(Guid referralId, IEnumerable<ExternalCheckType> types, Guid? actorUserId)
    {
        var list = (types ?? Array.Empty<ExternalCheckType>()).Distinct().ToList();
        if (list.Count == 0) return;

        // Fetch referral once (with patient/facility for providers).
        ReferralEntity? referral;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            referral = await db.Referrals
                .Include(r => r.Patient)
                .Include(r => r.Facility)
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == referralId);
        }
        if (referral == null) return;

        foreach (var type in list)
        {
            _ = RunOneAsync(referral, type, actorUserId);
        }
    }

    private async Task RunOneAsync(ReferralEntity referral, ExternalCheckType type, Guid? actorUserId)
    {
        var provider = _providers.TryGetValue(type, out var p) ? p : null;
        var correlationId = Guid.NewGuid().ToString("N");

        // Create pending record + event
        Guid checkId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var check = new ExternalCheckEntity
            {
                ReferralId = referral.Id,
                Type = type,
                ResultStatus = ExternalCheckResultStatus.Pending,
                RequestedAt = DateTime.UtcNow,
                Provider = provider?.ProviderName ?? "MissingProvider",
                CorrelationId = correlationId
            };

            db.ExternalChecks.Add(check);
            db.ReferralEvents.Add(new ReferralEventEntity
            {
                ReferralId = referral.Id,
                Type = ReferralEventType.ExternalCheckRequested,
                ActorUserId = actorUserId,
                At = DateTime.UtcNow,
                PayloadJson = $"{{\"type\":{(int)type},\"correlationId\":\"{correlationId}\"}}"
            });

            await db.SaveChangesAsync();
            checkId = check.Id;
        }

        ExternalCheckRunResult result;
        try
        {
            if (provider == null)
            {
                result = new ExternalCheckRunResult
                {
                    Status = ExternalCheckResultStatus.Failed,
                    ResultJson = "{\"error\":\"no_provider\"}"
                };
            }
            else
            {
                result = await provider.RunAsync(referral, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            result = new ExternalCheckRunResult
            {
                Status = ExternalCheckResultStatus.Failed,
                ResultJson = $"{{\"error\":\"exception\",\"message\":\"{EscapeJson(ex.Message)}\"}}"
            };
        }

        // Update record + completion event
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var check = await db.ExternalChecks.FirstOrDefaultAsync(c => c.Id == checkId);
            if (check == null) return;

            check.ResultStatus = result.Status;
            check.CompletedAt = DateTime.UtcNow;
            check.ResultJson = result.ResultJson;

            db.ReferralEvents.Add(new ReferralEventEntity
            {
                ReferralId = referral.Id,
                Type = ReferralEventType.ExternalCheckCompleted,
                ActorUserId = actorUserId,
                At = DateTime.UtcNow,
                PayloadJson = $"{{\"type\":{(int)type},\"status\":{(int)result.Status},\"correlationId\":\"{correlationId}\"}}"
            });

            await db.SaveChangesAsync();
        }
    }

    private static string EscapeJson(string value)
        => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}

