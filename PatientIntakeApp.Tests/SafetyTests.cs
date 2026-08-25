using PatientIntakeApp.Services;

namespace PatientIntakeApp.Tests;

public class SafetyTests
{
    [Fact]
    public void ValidationRejectsUnsafeDocumentsAndIncompleteFacilities()
    {
        Assert.True(IntakeValidator.ValidateDocument("SYNTHETIC_PACKET.pdf", 1024).IsValid);
        Assert.Contains("Only PDF", IntakeValidator.ValidateDocument("notes.txt", 1024).Errors.Single());
        Assert.Contains("empty", IntakeValidator.ValidateDocument("packet.pdf", 0).Errors.Single());
        Assert.False(IntakeValidator.ValidateFacility("FACILITY_ALPHA", []).IsValid);
        Assert.True(IntakeValidator.ValidateFacility("FACILITY_ALPHA", ["Synthetic review rule"]).IsValid);
    }

    [Fact]
    public void RedactionRemovesCommonSensitiveIdentifiers()
    {
        const string input = "PATIENT_0001, 123-45-6789, synthetic@example.test, (404) 555-0199";
        var output = SensitiveDataRedactor.Redact(input);
        Assert.DoesNotContain("123-45-6789", output);
        Assert.DoesNotContain("synthetic@example.test", output);
        Assert.DoesNotContain("404", output);
        Assert.DoesNotContain("PATIENT_0001", output);
        Assert.Equal("[PATIENT ID REDACTED], [SSN REDACTED], [EMAIL REDACTED], [PHONE REDACTED]", output);
    }
}

