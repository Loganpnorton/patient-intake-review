using Microsoft.EntityFrameworkCore;
using PatientIntakeApp.Data;
using PatientIntakeApp.Data.Entities;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Services.Stores;

public interface IReviewStore
{
    Task<ReviewSessionEntity> CreateSessionAsync(Guid referralId, Guid reviewerUserId, string? aiOverviewRaw);
    Task SaveSessionNarrativeAsync(Guid reviewSessionId, string? aiOverviewEdited, string? smeNotes);
    Task SaveSessionRawOverviewIfEmptyAsync(Guid reviewSessionId, string? aiOverviewRaw);
    Task SaveFindingsSnapshotAsync(Guid reviewSessionId, IEnumerable<Finding> findings);
    Task SetSessionPausedAsync(Guid reviewSessionId, bool paused, string? pauseReason, Guid? actorUserId);
    Task CompleteSessionAsync(Guid reviewSessionId, Guid? actorUserId);
    Task<(ReviewSessionEntity Session, List<FindingEntity> Findings)?> GetLatestSessionWithFindingsAsync(Guid referralId);
    Task<ReviewSessionEntity?> GetSessionAsync(Guid reviewSessionId);
}

public class ReviewStore : IReviewStore
{
    private readonly IDbContextFactory<PatientIntakeDbContext> _dbFactory;

    public ReviewStore(IDbContextFactory<PatientIntakeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ReviewSessionEntity> CreateSessionAsync(Guid referralId, Guid reviewerUserId, string? aiOverviewRaw)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.Id == referralId);
        if (referral == null) throw new InvalidOperationException("Referral not found.");

        var session = new ReviewSessionEntity
        {
            ReferralId = referralId,
            ReviewerUserId = reviewerUserId,
            StartedAt = DateTime.UtcNow,
            State = ReviewSessionState.InProgress,
            AiOverviewRaw = aiOverviewRaw,
            AiOverviewEdited = null
        };
        db.ReviewSessions.Add(session);

        referral.Status = ReferralStatus.InProgress;
        referral.UpdatedAt = DateTime.UtcNow;

        db.ReferralEvents.Add(new ReferralEventEntity
        {
            ReferralId = referralId,
            Type = ReferralEventType.StatusChanged,
            ActorUserId = reviewerUserId,
            At = DateTime.UtcNow,
            PayloadJson = $"{{\"status\":{(int)ReferralStatus.InProgress}}}"
        });

        await db.SaveChangesAsync();
        return session;
    }

    public async Task SaveSessionNarrativeAsync(Guid reviewSessionId, string? aiOverviewEdited, string? smeNotes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.ReviewSessions.FirstOrDefaultAsync(s => s.Id == reviewSessionId);
        if (session == null) return;

        session.AiOverviewEdited = string.IsNullOrWhiteSpace(aiOverviewEdited) ? null : aiOverviewEdited.Trim();
        session.SmeNotes = string.IsNullOrWhiteSpace(smeNotes) ? null : smeNotes.Trim();
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task SaveSessionRawOverviewIfEmptyAsync(Guid reviewSessionId, string? aiOverviewRaw)
    {
        if (string.IsNullOrWhiteSpace(aiOverviewRaw)) return;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.ReviewSessions.FirstOrDefaultAsync(s => s.Id == reviewSessionId);
        if (session == null) return;

        if (!string.IsNullOrWhiteSpace(session.AiOverviewRaw) || !string.IsNullOrWhiteSpace(session.AiOverviewEdited))
        {
            return;
        }

        session.AiOverviewRaw = aiOverviewRaw.Trim();
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task SaveFindingsSnapshotAsync(Guid reviewSessionId, IEnumerable<Finding> findings)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.ReviewSessions.FirstOrDefaultAsync(s => s.Id == reviewSessionId);
        if (session == null) return;

        // Replace snapshot each save.
        // IMPORTANT: use a bulk delete to avoid per-row concurrency exceptions when two saves overlap.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Findings WHERE ReviewSessionId = {0}", reviewSessionId);

        foreach (var f in findings ?? Array.Empty<Finding>())
        {
            db.Findings.Add(new FindingEntity
            {
                ReviewSessionId = reviewSessionId,
                Term = f.Term ?? string.Empty,
                Category = f.Category ?? string.Empty,
                Page = f.Page,
                Context = f.Context,
                Source = f.Source,
                MatchIndex = f.MatchIndex,
                Severity = f.Severity,
                ReviewStatus = f.ReviewStatus,
                IsReviewed = f.IsReviewed,
                IsFalseFlag = f.IsFalseFlag,
                FalseFlagReason = f.FalseFlagReason,
                ReviewedAt = f.IsReviewed ? DateTime.UtcNow : null
            });
        }

        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task SetSessionPausedAsync(Guid reviewSessionId, bool paused, string? pauseReason, Guid? actorUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.ReviewSessions.FirstOrDefaultAsync(s => s.Id == reviewSessionId);
        if (session == null) return;

        session.State = paused ? ReviewSessionState.Paused : ReviewSessionState.InProgress;
        session.PausedAt = paused ? DateTime.UtcNow : null;
        session.PauseReason = paused ? (string.IsNullOrWhiteSpace(pauseReason) ? "Paused" : pauseReason.Trim()) : null;
        session.UpdatedAt = DateTime.UtcNow;

        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.Id == session.ReferralId);
        if (referral != null)
        {
            referral.Status = paused ? ReferralStatus.Paused : ReferralStatus.InProgress;
            referral.UpdatedAt = DateTime.UtcNow;
        }

        db.ReferralEvents.Add(new ReferralEventEntity
        {
            ReferralId = session.ReferralId,
            Type = paused ? ReferralEventType.ReviewPaused : ReferralEventType.ReviewResumed,
            ActorUserId = actorUserId,
            At = DateTime.UtcNow,
            PayloadJson = paused ? $"{{\"reason\":\"{EscapeJson(session.PauseReason ?? "Paused")}\"}}" : null
        });

        await db.SaveChangesAsync();
    }

    public async Task CompleteSessionAsync(Guid reviewSessionId, Guid? actorUserId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.ReviewSessions.FirstOrDefaultAsync(s => s.Id == reviewSessionId);
        if (session == null) return;

        session.State = ReviewSessionState.Completed;
        session.CompletedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;

        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.Id == session.ReferralId);
        if (referral != null)
        {
            referral.Status = ReferralStatus.Completed;
            referral.UpdatedAt = DateTime.UtcNow;
        }

        db.ReferralEvents.Add(new ReferralEventEntity
        {
            ReferralId = session.ReferralId,
            Type = ReferralEventType.ReviewCompleted,
            ActorUserId = actorUserId,
            At = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    public async Task<(ReviewSessionEntity Session, List<FindingEntity> Findings)?> GetLatestSessionWithFindingsAsync(Guid referralId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.ReviewSessions
            .Include(s => s.Findings)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(s => s.ReferralId == referralId);

        if (session == null) return null;
        return (session, session.Findings.ToList());
    }

    public async Task<ReviewSessionEntity?> GetSessionAsync(Guid reviewSessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ReviewSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == reviewSessionId);
    }

    private static string EscapeJson(string value)
        => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}

