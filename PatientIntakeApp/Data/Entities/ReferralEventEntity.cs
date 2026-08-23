namespace PatientIntakeApp.Data.Entities;

public class ReferralEventEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReferralId { get; set; }
    public ReferralEntity? Referral { get; set; }

    public ReferralEventType Type { get; set; } = ReferralEventType.Created;

    public Guid? ActorUserId { get; set; }
    public UserEntity? ActorUser { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;

    public string? PayloadJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? RowVersion { get; set; }
}

