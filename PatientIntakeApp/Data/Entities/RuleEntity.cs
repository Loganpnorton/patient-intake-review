namespace PatientIntakeApp.Data.Entities;

public class RuleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FacilityId { get; set; }
    public FacilityEntity? Facility { get; set; }

    public RuleKind Kind { get; set; } = RuleKind.Keyword;

    public string Text { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public RuleSeverity Severity { get; set; } = RuleSeverity.Yellow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public byte[]? RowVersion { get; set; }
}

