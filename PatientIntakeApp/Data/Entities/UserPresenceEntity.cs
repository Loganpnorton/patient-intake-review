namespace PatientIntakeApp.Data.Entities;

public class UserPresenceEntity
{
    public Guid UserId { get; set; }
    public UserEntity? User { get; set; }

    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}

