namespace PatientIntakeApp.Data.Entities;

public class ReferralEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FacilityId { get; set; }
    public FacilityEntity? Facility { get; set; }

    public Guid PatientId { get; set; }
    public PatientEntity? Patient { get; set; }

    public string SourceFileName { get; set; } = string.Empty;
    public string SourceFilePath { get; set; } = string.Empty; // UNC/local path for now (DB is not storing PDF bytes in MVP)
    public string SourceFileHash { get; set; } = string.Empty; // SHA-256 hex

    public DateTime IngestedAt { get; set; } = DateTime.UtcNow;

    public ReferralStatus Status { get; set; } = ReferralStatus.New;

    public Guid? CurrentAssigneeUserId { get; set; }
    public UserEntity? CurrentAssigneeUser { get; set; }

    public DateTime? LockedUntil { get; set; }

    public Guid? DuplicateOfReferralId { get; set; }
    public ReferralEntity? DuplicateOfReferral { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? RowVersion { get; set; }

    public List<ReviewSessionEntity> ReviewSessions { get; set; } = new();
    public List<ReferralEventEntity> Events { get; set; } = new();
    public List<ExternalCheckEntity> ExternalChecks { get; set; } = new();
}

