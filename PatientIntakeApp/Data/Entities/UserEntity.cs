using System.ComponentModel.DataAnnotations;

namespace PatientIntakeApp.Data.Entities;

public class UserEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(64)]
    public string Username { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? DisplayName { get; set; }

    // Demo/test logins only. Replace with real auth later.
    [MaxLength(256)]
    public string PasswordHash { get; set; } = string.Empty;

    [MaxLength(256)]
    public string PasswordSalt { get; set; } = string.Empty;

    public int Role { get; set; } = 0;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? RowVersion { get; set; }
}

