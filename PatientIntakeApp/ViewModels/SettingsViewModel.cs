using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatientIntakeApp.Data.Entities;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services;
using PatientIntakeApp.Services.Stores;

namespace PatientIntakeApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IConfigurationService _configService;
    private readonly IThemeService _themeService;
    private readonly IRuleStore _ruleStore;
    private bool _suppressAiSettingAutosave;

    [ObservableProperty]
    private string _selectedSection = "Appearance";

    [ObservableProperty]
    private bool _isDarkMode;

    [ObservableProperty]
    private ObservableCollection<Facility> _facilities = new();

    public bool CanManageFacilities => _mainViewModel.CurrentUser?.Role is UserRole.Admin or UserRole.Developer;
    public bool CanSeeDeveloperOptions => _mainViewModel.CurrentUser?.Role == UserRole.Developer;
    public DevSettings DevSettings => _mainViewModel.DevSettings;

    public IReadOnlyList<int> AiBatchPageLimitOptions { get; } = new List<int> { 1, 2, 3, 4, 5, 10 };

    /// <summary>
    /// Available Gemini models for the dropdown. The app currently uses only gemini-3.1-flash-lite
    /// to avoid high-demand 503 errors. Additional models can be added here when available.
    /// </summary>
    public IReadOnlyList<string> GeminiModelOptions { get; } = new List<string>
    {
        "gemini-3.1-flash-lite"
    };

    [ObservableProperty]
    private string _geminiApiKey = string.Empty;

    [ObservableProperty]
    private string _selectedGeminiModel = "gemini-3.1-flash-lite";

    [ObservableProperty]
    private string _dbConnectionString = string.Empty;

    [ObservableProperty]
    private System.ComponentModel.ICollectionView? _facilitiesView;

    [ObservableProperty]
    private string _facilitySearchText = string.Empty;

    [ObservableProperty]
    private Facility? _selectedFacility;

    [ObservableProperty]
    private string _facilityId = string.Empty;

    [ObservableProperty]
    private string _facilityName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<RuleItem> _rules = new();

    [ObservableProperty]
    private string _newRuleText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<RuleItem> _contextRules = new();

    public ObservableCollection<RuleSeverity> SeverityOptions { get; } = new ObservableCollection<RuleSeverity>
    {
        RuleSeverity.Green,
        RuleSeverity.Yellow,
        RuleSeverity.Red
    };

    public enum RuleKind
    {
        Keyword = 0,
        Context = 1
    }

    public ObservableCollection<RuleKind> RuleKindOptions { get; } = new ObservableCollection<RuleKind>
    {
        RuleKind.Keyword,
        RuleKind.Context
    };

    [ObservableProperty]
    private RuleKind _selectedRuleKind = RuleKind.Keyword;

    [ObservableProperty]
    private bool _isCreatingNewFacility;

    private bool _suppressRuleAutosave;

    private bool CanSaveFacilities()
    {
        if (!CanManageFacilities) return false;
        if (SelectedFacility == null && !IsCreatingNewFacility) return false;

        var id = FacilityId?.Trim() ?? string.Empty;
        var name = FacilityName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Ensure ID is unique (case-insensitive), excluding the currently selected facility (when editing).
        return !Facilities.Any(f => !(SelectedFacility != null && ReferenceEquals(f, SelectedFacility)) &&
                                    string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public SettingsViewModel(
        MainViewModel mainViewModel,
        IConfigurationService configService,
        IThemeService themeService,
        IRuleStore ruleStore)
    {
        _mainViewModel = mainViewModel;
        _configService = configService;
        _themeService = themeService;
        _ruleStore = ruleStore;

        IsDarkMode = _configService.GetDarkModeEnabled();

        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(CanManageFacilities));
        OnPropertyChanged(nameof(CanSeeDeveloperOptions));
        OnPropertyChanged(nameof(DevSettings));

        Facilities = new ObservableCollection<Facility>(_configService.GetFacilities());
        FacilitiesView = CollectionViewSource.GetDefaultView(Facilities);
        FacilitiesView.Filter = FilterFacility;

        IsDarkMode = _configService.GetDarkModeEnabled();

        // Developer-only: load persisted AI settings (can be overridden by env vars at runtime).
        _suppressAiSettingAutosave = true;
        try
        {
            GeminiApiKey = _configService.GetSavedApiKey() ?? string.Empty;
            // Show the effective model from config
            SelectedGeminiModel = _configService.AiModel;
            DbConnectionString = _configService.GetDbConnectionString() ?? string.Empty;
        }
        finally
        {
            _suppressAiSettingAutosave = false;
        }

        // If the selected section is no longer permitted for this role, bounce back to Appearance.
        if (SelectedSection == "Facilities" && !CanManageFacilities)
        {
            SelectedSection = "Appearance";
        }
        if (SelectedSection == "DeveloperOptions" && !CanSeeDeveloperOptions)
        {
            SelectedSection = "Appearance";
        }

        // Keep selection coherent
        if (SelectedFacility != null)
        {
            SelectedFacility = Facilities.FirstOrDefault(f => f.Id == SelectedFacility.Id);
        }
    }

    partial void OnGeminiApiKeyChanged(string value)
    {
        if (!CanSeeDeveloperOptions) return;
        if (_suppressAiSettingAutosave) return;
        try { _configService.SaveApiKey(value); } catch { }
    }

    partial void OnSelectedGeminiModelChanged(string value)
    {
        if (!CanSeeDeveloperOptions) return;
        if (_suppressAiSettingAutosave) return;
        try { _configService.SaveAiModel(value); } catch { }
    }

    partial void OnDbConnectionStringChanged(string value)
    {
        if (!CanSeeDeveloperOptions) return;
        if (_suppressAiSettingAutosave) return;
        try { _configService.SaveDbConnectionString(value); } catch { }
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        _themeService.ApplyDarkMode(value);
        _configService.SetDarkModeEnabled(value);
    }

    partial void OnFacilitySearchTextChanged(string value)
    {
        FacilitiesView?.Refresh();
    }

    private bool FilterFacility(object obj)
    {
        if (obj is not Facility f) return false;
        if (string.IsNullOrWhiteSpace(FacilitySearchText)) return true;
        return f.Name.Contains(FacilitySearchText, System.StringComparison.OrdinalIgnoreCase) ||
               f.Id.Contains(FacilitySearchText, System.StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSelectedFacilityChanged(Facility? value)
    {
        if (value == null)
        {
            FacilityId = string.Empty;
            FacilityName = string.Empty;
            Rules = new ObservableCollection<RuleItem>();
            ContextRules = new ObservableCollection<RuleItem>();
            SelectedRuleKind = RuleKind.Keyword;
            return;
        }

        IsCreatingNewFacility = false;
        FacilityId = value.Id;
        FacilityName = value.Name;
        _suppressRuleAutosave = true;
        Rules = new ObservableCollection<RuleItem>();
        ContextRules = new ObservableCollection<RuleItem>();
        HookRuleAutosave();
        _suppressRuleAutosave = false;
        SelectedRuleKind = RuleKind.Keyword;

        // Load rules from the shared DB (supports enable/disable + severity).
        _ = LoadRulesFromDbAsync(value);

        SaveFacilitiesCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadRulesFromDbAsync(Facility facility)
    {
        if (facility == null) return;
        if (string.IsNullOrWhiteSpace(facility.Id)) return;

        try
        {
            _suppressRuleAutosave = true;

            var keyword = await _ruleStore.ListRulesAsync(facility.Id, PatientIntakeApp.Data.Entities.RuleKind.Keyword);
            var context = await _ruleStore.ListRulesAsync(facility.Id, PatientIntakeApp.Data.Entities.RuleKind.Context);

            if (keyword.Count == 0 && context.Count == 0)
            {
                // No rules in DB yet - fall back to config.json text rules
                Rules = new ObservableCollection<RuleItem>((facility.Rules ?? new()).Select(r => new RuleItem(Guid.Empty, r, true, RuleSeverity.Yellow)));
                ContextRules = new ObservableCollection<RuleItem>((facility.ContextRules ?? new()).Select(r => new RuleItem(Guid.Empty, r, true, RuleSeverity.Yellow)));
            }
            else
            {
                Rules = new ObservableCollection<RuleItem>(keyword.Select(r => new RuleItem(r.Id, r.Text, r.IsEnabled, r.Severity)));
                ContextRules = new ObservableCollection<RuleItem>(context.Select(r => new RuleItem(r.Id, r.Text, r.IsEnabled, r.Severity)));
            }

            HookRuleAutosave();
        }
        catch
        {
            // Non-fatal: fall back to config.json text rules if DB read fails.
            Rules = new ObservableCollection<RuleItem>((facility.Rules ?? new()).Select(r => new RuleItem(Guid.Empty, r, true, RuleSeverity.Yellow)));
            ContextRules = new ObservableCollection<RuleItem>((facility.ContextRules ?? new()).Select(r => new RuleItem(Guid.Empty, r, true, RuleSeverity.Yellow)));
            HookRuleAutosave();
        }
        finally
        {
            _suppressRuleAutosave = false;
        }
    }

    private void HookRuleAutosave()
    {
        Rules.CollectionChanged -= Rules_CollectionChanged;
        ContextRules.CollectionChanged -= ContextRules_CollectionChanged;
        Rules.CollectionChanged += Rules_CollectionChanged;
        ContextRules.CollectionChanged += ContextRules_CollectionChanged;

        foreach (var r in Rules)
        {
            r.PropertyChanged -= RuleItem_PropertyChanged;
            r.PropertyChanged += RuleItem_PropertyChanged;
        }
        foreach (var r in ContextRules)
        {
            r.PropertyChanged -= RuleItem_PropertyChanged;
            r.PropertyChanged += RuleItem_PropertyChanged;
        }
    }

    private void Rules_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        HookRuleAutosave();
    }

    private void ContextRules_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        HookRuleAutosave();
    }

    private void RuleItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RuleItem rule) return;

        if (e.PropertyName == nameof(RuleItem.Text) ||
            e.PropertyName == nameof(RuleItem.IsEnabled) ||
            e.PropertyName == nameof(RuleItem.Severity))
        {
            if (_suppressRuleAutosave) return;
            if (!CanManageFacilities) return;
            if (SelectedFacility == null) return;
            if (IsCreatingNewFacility) return;

            // Fire-and-forget: persist rule edits to shared DB.
            _ = PersistRuleAsync(rule);
        }
    }

    private async Task PersistRuleAsync(RuleItem rule)
    {
        try
        {
            if (SelectedFacility == null) return;
            if (rule.Id == Guid.Empty) return; // imported from fallback; don't attempt to persist without a DB row

            await _ruleStore.UpdateRuleAsync(rule.Id, rule.Text, rule.IsEnabled, rule.Severity);
        }
        catch
        {
            // Non-fatal
        }
    }

    partial void OnFacilityIdChanged(string value)
    {
        if (SelectedFacility == null && !string.IsNullOrWhiteSpace(value))
        {
            IsCreatingNewFacility = true;
        }
        SaveFacilitiesCommand.NotifyCanExecuteChanged();
    }

    partial void OnFacilityNameChanged(string value)
    {
        if (SelectedFacility == null && !string.IsNullOrWhiteSpace(value))
        {
            IsCreatingNewFacility = true;
        }
        SaveFacilitiesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddFacility()
    {
        if (!CanManageFacilities) return;

        // Draft mode: do NOT add to the list until the user saves valid required fields.
        SelectedFacility = null;
        IsCreatingNewFacility = true;
        FacilityId = string.Empty;
        FacilityName = string.Empty;
        Rules = new ObservableCollection<RuleItem>();
        ContextRules = new ObservableCollection<RuleItem>();
        NewRuleText = string.Empty;
        SaveFacilitiesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RemoveFacility(Facility facility)
    {
        if (!CanManageFacilities) return;
        if (facility == null) return;

        var name = string.IsNullOrWhiteSpace(facility.Name) ? "(Unnamed facility)" : facility.Name;
        var ok = await _mainViewModel.ShowConfirmAsync("Confirm Remove", $"Remove facility '{name}'?", iconKind: "AlertCircleOutline");
        if (!ok) return;

        var wasSelected = ReferenceEquals(SelectedFacility, facility);
        Facilities.Remove(facility);
        FacilitiesView?.Refresh();

        if (wasSelected)
        {
            SelectedFacility = Facilities.FirstOrDefault();
        }

        _configService.SaveFacilities(Facilities.ToList());
    }

    [RelayCommand(CanExecute = nameof(CanSaveFacilities))]
    private void SaveFacilities()
    {
        if (!CanManageFacilities) return;

        var id = FacilityId.Trim();
        var name = FacilityName.Trim();
        var rules = Rules
            .Select(r => r.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contextRules = ContextRules
            .Select(r => r.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (IsCreatingNewFacility)
        {
            var created = new Facility { Id = id, Name = name, Rules = rules, ContextRules = contextRules };
            Facilities.Add(created);
            SelectedFacility = created;
            IsCreatingNewFacility = false;
        }
        else if (SelectedFacility != null)
        {
            // Apply edits into selected facility (replace object so list updates immediately)
            var updated = new Facility { Id = id, Name = name, Rules = rules, ContextRules = contextRules };

            var idx = Facilities.IndexOf(SelectedFacility);
            if (idx >= 0)
            {
                Facilities[idx] = updated;
                SelectedFacility = updated;
            }
        }

        _configService.SaveFacilities(Facilities.ToList());
        FacilitiesView?.Refresh();
    }

    [RelayCommand]
    private void AddRule()
    {
        if (!CanManageFacilities) return;
        if (SelectedFacility == null && !IsCreatingNewFacility) return;

        var text = NewRuleText.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (IsCreatingNewFacility)
        {
            // Draft mode: add directly to in-memory collections (will be persisted when saved)
            var item = new RuleItem(Guid.Empty, text, true, RuleSeverity.Yellow);
            if (SelectedRuleKind == RuleKind.Context)
            {
                if (ContextRules.Any(r => string.Equals(r.Text, text, StringComparison.OrdinalIgnoreCase))) return;
                ContextRules.Add(item);
            }
            else
            {
                if (Rules.Any(r => string.Equals(r.Text, text, StringComparison.OrdinalIgnoreCase))) return;
                Rules.Add(item);
            }
            NewRuleText = string.Empty;
        }
        else
        {
            _ = AddRuleToDbAsync(text, SelectedRuleKind);
        }
    }

    private async Task AddRuleToDbAsync(string text, RuleKind kind)
    {
        try
        {
            if (SelectedFacility == null) return;
            var dbKind = kind == RuleKind.Context ? PatientIntakeApp.Data.Entities.RuleKind.Context : PatientIntakeApp.Data.Entities.RuleKind.Keyword;
            await _ruleStore.AddRuleAsync(SelectedFacility.Id, dbKind, text, isEnabled: true, severity: RuleSeverity.Yellow);
            NewRuleText = string.Empty;
            await LoadRulesFromDbAsync(SelectedFacility);
        }
        catch
        {
            // Non-fatal
        }
    }

    [RelayCommand]
    private void RemoveContextRule(RuleItem rule)
    {
        if (!CanManageFacilities) return;
        if (rule == null) return;
        _ = DeleteRuleFromDbAsync(rule, isContext: true);
    }

    [RelayCommand]
    private void RemoveRule(RuleItem rule)
    {
        if (!CanManageFacilities) return;
        if (rule == null) return;
        _ = DeleteRuleFromDbAsync(rule, isContext: false);
    }

    private async Task DeleteRuleFromDbAsync(RuleItem rule, bool isContext)
    {
        try
        {
            if (rule.Id != Guid.Empty)
            {
                await _ruleStore.DeleteRuleAsync(rule.Id);
            }
        }
        catch
        {
            // ignore
        }

        if (isContext) ContextRules.Remove(rule);
        else Rules.Remove(rule);
    }

    [RelayCommand]
    private void ImportRules()
    {
        if (!CanManageFacilities) return;
        if (SelectedFacility == null && !IsCreatingNewFacility) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON or CSV (*.json;*.csv)|*.json;*.csv|All Files (*.*)|*.*",
            Multiselect = false,
            Title = "Import Rules (JSON or CSV)"
        };

        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);

        var imported = ext == ".json" ? ParseRulesFromJson(content) : ParseRulesFromCsv(content);

        if (IsCreatingNewFacility)
        {
            // Draft mode: add directly to in-memory Keyword Rules collection
            foreach (var rule in imported)
            {
                if (!Rules.Any(r => string.Equals(r.Text, rule, StringComparison.OrdinalIgnoreCase)))
                {
                    Rules.Add(new RuleItem(Guid.Empty, rule, true, RuleSeverity.Yellow));
                }
            }
        }
        else
        {
            _ = ImportRulesToDbAsync(imported);
        }
    }

    private async Task ImportRulesToDbAsync(List<string> imported)
    {
        try
        {
            if (SelectedFacility == null) return;
            foreach (var rule in imported)
            {
                await _ruleStore.AddRuleAsync(SelectedFacility.Id, PatientIntakeApp.Data.Entities.RuleKind.Keyword, rule, isEnabled: true, severity: RuleSeverity.Yellow);
            }
            await LoadRulesFromDbAsync(SelectedFacility);
        }
        catch
        {
            // ignore
        }
    }

    private static List<string> ParseRulesFromCsv(string content)
    {
        return content
            .Split(new[] { '\r', '\n', ',' }, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ParseRulesFromJson(string content)
    {
        try
        {
            content = content.Trim();

            // Case 1: ["rule1", "rule2"]
            if (content.StartsWith("["))
            {
                var arr = JsonSerializer.Deserialize<List<string>>(content);
                return (arr ?? new List<string>()).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
            }

            // Case 2: { "rules": [...] } or { "Rules": [...] } or facility-like object
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (!doc.RootElement.TryGetProperty("Rules", out var rulesProp))
                {
                    doc.RootElement.TryGetProperty("rules", out rulesProp);
                }

                if (rulesProp.ValueKind == JsonValueKind.Array)
                {
                    var list = rulesProp.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString() ?? "")
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(System.StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return list;
                }
            }
        }
        catch
        {
            // Fall through to empty list
        }

        return new List<string>();
    }

    [RelayCommand]
    private async Task ShowImportHelp()
    {
        var message =
            "You can import rules from CSV or JSON.\n\n" +
            "CSV (one rule per line or comma-separated):\n" +
            "Methadone\n" +
            "Violent\n" +
            "Aggressive\n\n" +
            "Or:\n" +
            "Methadone, Violent, Aggressive\n\n" +
            "JSON (array of strings):\n" +
            "[\n" +
            "  \"Methadone\",\n" +
            "  \"Violent\",\n" +
            "  \"Aggressive\"\n" +
            "]\n\n" +
            "JSON (object with rules):\n" +
            "{\n" +
            "  \"rules\": [\"Methadone\", \"Violent\", \"Aggressive\"]\n" +
            "}\n\n" +
            "Notes:\n" +
            "- Rules are de-duplicated (case-insensitive).\n" +
            "- Empty entries are ignored.";

        await _mainViewModel.ShowInfoAsync("Rule Import Formats", message, iconKind: "HelpCircleOutline");
    }
}

public partial class RuleItem : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private RuleSeverity _severity;

    public RuleItem(Guid id, string text, bool isEnabled, RuleSeverity severity)
    {
        Id = id;
        _text = text;
        _isEnabled = isEnabled;
        _severity = severity;
    }
}


