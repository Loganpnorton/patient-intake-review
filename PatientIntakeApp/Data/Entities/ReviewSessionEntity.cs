namespace PatientIntakeApp.Data.Entities;

public class ReviewSessionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReferralId { get; set; }
    public ReferralEntity? Referral { get; set; }

    public Guid ReviewerUserId { get; set; }
    public UserEntity? ReviewerUser { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ReviewSessionState State { get; set; } = ReviewSessionState.InProgress;

    public DateTime? PausedAt { get; set; }
    public string? PauseReason { get; set; }

    public string? SmeNotes { get; set; }

    public string? AiOverviewRaw { get; set; }
    public string? AiOverviewEdited { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? RowVersion { get; set; }

    public List<FindingEntity> Findings { get; set; } = new();
}

