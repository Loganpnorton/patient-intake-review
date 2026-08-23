using PatientIntakeApp.Models;

namespace PatientIntakeApp.Data.Entities;

public class FindingEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReviewSessionId { get; set; }
    public ReviewSessionEntity? ReviewSession { get; set; }

    public string Term { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Page { get; set; }
    public string? Context { get; set; }

    public FindingSource Source { get; set; } = FindingSource.Unknown;
    public int? MatchIndex { get; set; }

    public SeverityLevel Severity { get; set; } = SeverityLevel.Yellow;
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
    public bool IsReviewed { get; set; }

    public bool IsFalseFlag { get; set; }
    public string? FalseFlagReason { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? RowVersion { get; set; }
}

