using System.IO;
using System.Text.RegularExpressions;

namespace PatientIntakeApp.Services;

public record IntakeValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public static class IntakeValidator
{
    private const long MaxDocumentBytes = 25 * 1024 * 1024;

    public static IntakeValidationResult ValidateDocument(string? fileName, long sizeBytes)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(fileName)) errors.Add("A document name is required.");
        else if (!string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase)) errors.Add("Only PDF documents are accepted.");
        if (sizeBytes <= 0) errors.Add("The document is empty.");
        if (sizeBytes > MaxDocumentBytes) errors.Add("The document exceeds the 25 MB limit.");
        return new IntakeValidationResult(errors.Count == 0, errors);
    }

    public static IntakeValidationResult ValidateFacility(string? facilityId, IEnumerable<string>? rules)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(facilityId)) errors.Add("A facility must be selected.");
        if (rules is null || !rules.Any(rule => !string.IsNullOrWhiteSpace(rule))) errors.Add("At least one review rule is required.");
        return new IntakeValidationResult(errors.Count == 0, errors);
    }
}

public static partial class SensitiveDataRedactor
{
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<!\d)(?:\+?1[ .-]?)?\(?\d{3}\)?[ .-]\d{3}[ .-]\d{4}(?!\d)")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"\b(?:MRN|PATIENT)[-_ :]?[A-Z0-9]{4,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex PatientIdPattern();

    public static string Redact(string? text)
    {
        var redacted = text ?? string.Empty;
        redacted = SsnPattern().Replace(redacted, "[SSN REDACTED]");
        redacted = EmailPattern().Replace(redacted, "[EMAIL REDACTED]");
        redacted = PhonePattern().Replace(redacted, "[PHONE REDACTED]");
        return PatientIdPattern().Replace(redacted, "[PATIENT ID REDACTED]");
    }
}
