namespace PatientIntakeApp.Data.Entities;

public class ExternalCheckEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReferralId { get; set; }
    public ReferralEntity? Referral { get; set; }

    public ExternalCheckType Type { get; set; } = ExternalCheckType.Financial;
    public ExternalCheckResultStatus ResultStatus { get; set; } = ExternalCheckResultStatus.Pending;

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public string? Provider { get; set; }
    public string? CorrelationId { get; set; }

    public string? ResultJson { get; set; }

    public byte[]? RowVersion { get; set; }
}

