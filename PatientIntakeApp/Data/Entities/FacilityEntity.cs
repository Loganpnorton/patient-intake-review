namespace PatientIntakeApp.Data.Entities;

public class FacilityEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Bridges existing config.json Facility.Id (e.g., "FAC-001") during migration.
    public string? LegacyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? RowVersion { get; set; }

    public List<RuleEntity> Rules { get; set; } = new();
    public List<ReferralEntity> Referrals { get; set; } = new();
}

