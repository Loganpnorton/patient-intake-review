using PatientIntakeApp.Models;
using PatientIntakeApp.Services;

namespace PatientIntakeApp.Tests;

public class ConfigurationTests
{
    [Fact]
    public void SettingsRoundTripInAnIsolatedDirectory()
    {
        var original = Directory.GetCurrentDirectory();
        var temp = Directory.CreateTempSubdirectory("patient-intake-config-");
        try
        {
            Directory.SetCurrentDirectory(temp.FullName);
            var service = new ConfigurationService();
            Assert.Equal(2, service.GetFacilities().Count);
            service.SaveFacilities([new Facility { Id = "FACILITY_ALPHA", Name = "Synthetic Facility", Rules = ["Rule A"] }]);
            service.SetDarkModeEnabled(true);
            service.SaveApiKey("  synthetic-key  ");
            service.AddToRecentHistory("SYNTHETIC_PACKET.pdf");
            Assert.Single(service.GetFacilities());
            Assert.True(service.GetDarkModeEnabled());
            Assert.Equal("synthetic-key", service.GetSavedApiKey());
            Assert.Equal("SYNTHETIC_PACKET.pdf", service.GetRecentHistory().Single().FileName);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            temp.Delete(true);
        }
    }
}

