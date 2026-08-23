namespace PatientIntakeApp.Data.Entities;

public class PatientEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? ExternalMrn { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime? Dob { get; set; }
    public string NormalizedKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? RowVersion { get; set; }

    public List<ReferralEntity> Referrals { get; set; } = new();
}

