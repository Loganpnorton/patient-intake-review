using System.IO;
using System.Text.Json;
using PatientIntakeApp.Models;

namespace PatientIntakeApp.Services;

public interface IConfigurationService
{
    string ApiKey { get; }
    string AiModel { get; }
    string GetApiKeyDebugHint();
    string? GetSavedApiKey();
    void SaveApiKey(string? apiKey);
    string? GetSavedAiModel();
    void SaveAiModel(string? model);
    string? GetDbConnectionString();
    void SaveDbConnectionString(string? connectionString);
    List<Facility> GetFacilities();
    void SaveFacilities(List<Facility> facilities);
    string? GetLastSelectedFacilityId();
    void SetLastSelectedFacilityId(string facilityId);
    bool GetDarkModeEnabled();
    void SetDarkModeEnabled(bool enabled);
    List<RecentPatient> GetRecentHistory();
    void AddToRecentHistory(string fileName);
    DevSettings GetDevSettings();
    void SaveDevSettings(DevSettings settings);
}

public class ConfigurationService : IConfigurationService
{
    private const string ConfigFileName = "config.json";
    private const string SettingsFileName = "user_settings.json";
    private const string ForcedGeminiModel = "gemini-3.1-flash-lite";
    
    // Prefer environment variable, then persisted user settings.
    // NOTE: avoid hardcoding secrets in source code; kept only as a last-resort fallback.
    public string ApiKey
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            var settings = LoadSettings();
            if (!string.IsNullOrWhiteSpace(settings.ApiKey)) return settings.ApiKey.Trim();

            // No key configured.
            return string.Empty;
        }
    }

    public string AiModel
    {
        get
        {
            // Pinned to a single stable model to avoid runtime 404s from deprecated/unsupported names.
            // NOTE: GEMINI_MODEL and user_settings.json AiModel are intentionally ignored.
            return ForcedGeminiModel;
        }
    }

    public string? GetSavedApiKey() => LoadSettings().ApiKey;
    public void SaveApiKey(string? apiKey)
    {
        var settings = LoadSettings();
        settings.ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        SaveSettings(settings);
    }

    public string? GetSavedAiModel() => LoadSettings().AiModel;
    public void SaveAiModel(string? model)
    {
        var settings = LoadSettings();
        settings.AiModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        SaveSettings(settings);
    }

    // Prefer env var for shared DB connection string, then user settings.
    // Example (local SQL Server): Server=localhost;Database=PatientIntake;Trusted_Connection=True;TrustServerCertificate=True;
    // Example (SQL auth): Server=...;Database=...;User ID=...;Password=...;TrustServerCertificate=True;
    public string? GetDbConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("PATIENTINTAKE_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return LoadSettings().DbConnectionString;
    }

    public void SaveDbConnectionString(string? connectionString)
    {
        var settings = LoadSettings();
        settings.DbConnectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString.Trim();
        SaveSettings(settings);
    }

    public string GetApiKeyDebugHint()
    {
        var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return $"env:...{Last4(env.Trim())}";

        var settings = LoadSettings();
        if (!string.IsNullOrWhiteSpace(settings.ApiKey)) return $"user_settings.json:...{Last4(settings.ApiKey.Trim())}";

        return "missing";
    }

    private static string Last4(string value)
    {
        if (string.IsNullOrEmpty(value)) return "????";
        return value.Length <= 4 ? value : value.Substring(value.Length - 4);
    }

    public List<Facility> GetFacilities()
    {
        if (!File.Exists(ConfigFileName))
        {
            // Return default facilities if config doesn't exist
            return new List<Facility>
            {
                new Facility 
                { 
                    Id = "FAC-001", 
                    Name = "Downtown Clinic", 
                    Rules = new List<string> { "Methadone", "Violent", "Aggressive", "Non-compliant" } 
                },
                new Facility 
                { 
                    Id = "FAC-002", 
                    Name = "Recovery Center West", 
                    Rules = new List<string> { "Sores", "Wheelchair", "Bedbound" } 
                }
            };
        }

        try
        {
            var json = File.ReadAllText(ConfigFileName);
            return JsonSerializer.Deserialize<List<Facility>>(json) ?? new List<Facility>();
        }
        catch
        {
            return new List<Facility>();
        }
    }

    public void SaveFacilities(List<Facility> facilities)
    {
        try
        {
            var json = JsonSerializer.Serialize(facilities, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFileName, json);
        }
        catch
        {
            // Non-critical; callers can choose to report.
        }
    }

    public string? GetLastSelectedFacilityId()
    {
        if (!File.Exists(SettingsFileName)) return null;
        try
        {
            var json = File.ReadAllText(SettingsFileName);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);
            return settings?.LastSelectedFacilityId;
        }
        catch
        {
            return null;
        }
    }

    public void SetLastSelectedFacilityId(string facilityId)
    {
        var settings = LoadSettings();
        settings.LastSelectedFacilityId = facilityId;
        SaveSettings(settings);
    }

    public bool GetDarkModeEnabled()
    {
        return LoadSettings().IsDarkModeEnabled ?? false;
    }

    public void SetDarkModeEnabled(bool enabled)
    {
        var settings = LoadSettings();
        settings.IsDarkModeEnabled = enabled;
        SaveSettings(settings);
    }

    public List<RecentPatient> GetRecentHistory()
    {
        return LoadSettings().RecentHistory ?? new List<RecentPatient>();
    }

    public void AddToRecentHistory(string fileName)
    {
        var settings = LoadSettings();
        if (settings.RecentHistory == null) settings.RecentHistory = new List<RecentPatient>();

        // De-dupe by filename so the chips don't spam duplicates.
        settings.RecentHistory = settings.RecentHistory
            .Where(r => !string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        
        settings.RecentHistory.Insert(0, new RecentPatient { FileName = fileName, ProcessedDate = DateTime.Now });
        
        // Keep only last 5
        if (settings.RecentHistory.Count > 5)
        {
            settings.RecentHistory = settings.RecentHistory.Take(5).ToList();
        }
        
        SaveSettings(settings);
    }

    private UserSettings LoadSettings()
    {
        if (!File.Exists(SettingsFileName)) return new UserSettings();
        try
        {
            var json = File.ReadAllText(SettingsFileName);
            return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    private void SaveSettings(UserSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText(SettingsFileName, json);
    }

    public DevSettings GetDevSettings()
    {
        if (!File.Exists("dev_settings.json")) return new DevSettings();
        try
        {
            var json = File.ReadAllText("dev_settings.json");
            return JsonSerializer.Deserialize<DevSettings>(json) ?? new DevSettings();
        }
        catch
        {
            return new DevSettings();
        }
    }

    public void SaveDevSettings(DevSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText("dev_settings.json", json);
    }

    private class UserSettings
    {
        public string? LastSelectedFacilityId { get; set; }
        public bool? IsDarkModeEnabled { get; set; }
        public List<RecentPatient>? RecentHistory { get; set; }
        public string? ApiKey { get; set; }
        public string? AiModel { get; set; }
        public string? DbConnectionString { get; set; }
    }
}

