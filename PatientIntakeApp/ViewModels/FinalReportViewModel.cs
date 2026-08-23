using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services;
using PatientIntakeApp.Services.Stores;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace PatientIntakeApp.ViewModels;

public partial class FinalReportViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IReviewStore _reviewStore;
    private readonly IReferralStore _referralStore;
    private readonly IConfigurationService _configService;
    private readonly IPdfProcessingService _pdfService;
    private readonly IAnalysisService _analysisService;
    private Guid _reviewSessionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _sourceFileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeneratedAtDisplay))]
    private DateTime _generatedAt = DateTime.Now;

    [ObservableProperty]
    private string? _agentOverview;

    [ObservableProperty]
    private bool _isGeneratingNarrative;

    [ObservableProperty]
    private bool _isEditingOverview;

    [ObservableProperty]
    private string _editedOverview = string.Empty;

    [ObservableProperty]
    private string _smeNotes = string.Empty;

    partial void OnSmeNotesChanged(string value)
    {
        if (_reviewSessionId == Guid.Empty) return;
        // Best-effort persistence.
        _ = _reviewStore.SaveSessionNarrativeAsync(_reviewSessionId, aiOverviewEdited: AgentOverview, smeNotes: value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalFindings))]
    [NotifyPropertyChangedFor(nameof(LocalCount))]
    [NotifyPropertyChangedFor(nameof(AiCount))]
    [NotifyPropertyChangedFor(nameof(ClearedCount))]
    [NotifyPropertyChangedFor(nameof(FlaggedCount))]
    [NotifyPropertyChangedFor(nameof(PendingCount))]
    private ObservableCollection<Finding> _findings = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextViolationCount))]
    [NotifyPropertyChangedFor(nameof(HasContextViolations))]
    private ObservableCollection<Finding> _contextViolations = new();

    public FinalReportViewModel(
        MainViewModel mainViewModel,
        IReviewStore reviewStore,
        IReferralStore referralStore,
        IConfigurationService configService,
        IPdfProcessingService pdfService,
        IAnalysisService analysisService)
    {
        _mainViewModel = mainViewModel;
        _reviewStore = reviewStore;
        _referralStore = referralStore;
        _configService = configService;
        _pdfService = pdfService;
        _analysisService = analysisService;
    }

    public string Title => string.IsNullOrWhiteSpace(SourceFileName) ? "Review Summary" : $"Review Summary • {SourceFileName}";
    public string GeneratedAtDisplay => GeneratedAt.ToString("g");

    public int TotalFindings => Findings?.Count ?? 0;
    public int LocalCount => Findings?.Count(f => f.Source == FindingSource.Local) ?? 0;
    public int AiCount => Findings?.Count(f => f.Source == FindingSource.AI) ?? 0;

    public int ClearedCount => Findings?.Count(f => f.ReviewStatus == ReviewStatus.Passed) ?? 0;
    public int FlaggedCount => Findings?.Count(f => f.ReviewStatus == ReviewStatus.Rejected) ?? 0;
    public int PendingCount => Findings?.Count(f => f.ReviewStatus == ReviewStatus.Pending) ?? 0;

    public int ContextViolationCount => ContextViolations?.Count ?? 0;
    public bool HasContextViolations => ContextViolationCount > 0;

    public void Initialize(string? sourceFileName, IEnumerable<Finding> findings, string? agentOverview, IEnumerable<Finding> contextViolations, Guid? reviewSessionId)
    {
        SourceFileName = sourceFileName ?? string.Empty;
        GeneratedAt = DateTime.Now;
        _reviewSessionId = reviewSessionId ?? Guid.Empty;
        AgentOverview = string.IsNullOrWhiteSpace(agentOverview) ? null : agentOverview.Trim();
        EditedOverview = AgentOverview ?? string.Empty;
        IsEditingOverview = false;
        SmeNotes = string.Empty;

        // Sort for a stable, readable report.
        Findings = new ObservableCollection<Finding>(
            (findings ?? Enumerable.Empty<Finding>())
                .OrderBy(f => f.Page)
                .ThenBy(f => f.Source)
                .ThenBy(f => f.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Term, StringComparer.OrdinalIgnoreCase)
                .ToList());

        ContextViolations = new ObservableCollection<Finding>(
            (contextViolations ?? Enumerable.Empty<Finding>())
                .OrderBy(f => f.Page)
                .ThenBy(f => f.Term, StringComparer.OrdinalIgnoreCase)
                .ToList());

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(GeneratedAtDisplay));

        if (_reviewSessionId != Guid.Empty)
        {
            _ = LoadSessionAsync(_reviewSessionId, AgentOverview);
        }
    }

    private async Task LoadSessionAsync(Guid reviewSessionId, string? fallbackOverview)
    {
        try
        {
            var session = await _reviewStore.GetSessionAsync(reviewSessionId);
            if (session == null) return;

            var overview = session.AiOverviewEdited ?? session.AiOverviewRaw ?? fallbackOverview ?? string.Empty;
            AgentOverview = string.IsNullOrWhiteSpace(overview) ? null : overview.Trim();
            EditedOverview = AgentOverview ?? string.Empty;
            SmeNotes = session.SmeNotes ?? string.Empty;
        }
        catch
        {
            // non-fatal
        }
    }

    [RelayCommand]
    private async Task GenerateNarrative()
    {
        if (IsGeneratingNarrative) return;
        if (_reviewSessionId == Guid.Empty) return;

        IsGeneratingNarrative = true;
        try
        {
            var session = await _reviewStore.GetSessionAsync(_reviewSessionId);
            if (session == null) return;

            // If narrative already exists, don't overwrite (edits win).
            if (!string.IsNullOrWhiteSpace(session.AiOverviewEdited) || !string.IsNullOrWhiteSpace(session.AiOverviewRaw))
            {
                AgentOverview = (session.AiOverviewEdited ?? session.AiOverviewRaw)!.Trim();
                EditedOverview = AgentOverview ?? string.Empty;
                return;
            }

            var referral = await _referralStore.GetByIdAsync(session.ReferralId);
            if (referral == null) return;
            if (string.IsNullOrWhiteSpace(referral.SourceFilePath)) return;

            // Resolve facility for prompting.
            Facility? facility = null;
            var legacyFacilityId = referral.Facility?.LegacyId;
            if (!string.IsNullOrWhiteSpace(legacyFacilityId))
            {
                facility = _configService.GetFacilities().FirstOrDefault(f => f.Id == legacyFacilityId);
                if (facility != null)
                {
                    _mainViewModel.SelectedFacility = facility;
                }
            }

            facility ??= _mainViewModel.SelectedFacility;
            if (facility == null) return;

            // Generate overview (uses only a few representative pages, but we still extract page pdf bytes).
            var pages = await System.Threading.Tasks.Task.Run(() => _pdfService.ExtractText(referral.SourceFilePath));
            var overviewResult = await _analysisService.GenerateAgentOverviewAsync(pages, facility, _mainViewModel.DevSettings);
            var narrative = overviewResult?.Overview;
            if (string.IsNullOrWhiteSpace(narrative)) return;

            await _reviewStore.SaveSessionRawOverviewIfEmptyAsync(_reviewSessionId, narrative);

            AgentOverview = narrative.Trim();
            EditedOverview = AgentOverview ?? string.Empty;
        }
        catch
        {
            // non-fatal
        }
        finally
        {
            IsGeneratingNarrative = false;
        }
    }

    [RelayCommand]
    private void StartEditingOverview()
    {
        IsEditingOverview = true;
        EditedOverview = AgentOverview ?? string.Empty;
    }

    [RelayCommand]
    private async Task SaveEditedOverview()
    {
        AgentOverview = string.IsNullOrWhiteSpace(EditedOverview) ? null : EditedOverview.Trim();
        IsEditingOverview = false;

        if (_reviewSessionId != Guid.Empty)
        {
            await _reviewStore.SaveSessionNarrativeAsync(_reviewSessionId, aiOverviewEdited: AgentOverview, smeNotes: SmeNotes);
        }
    }

    [RelayCommand]
    private void CancelEditingOverview()
    {
        EditedOverview = AgentOverview ?? string.Empty;
        IsEditingOverview = false;
    }

    [RelayCommand]
    private void CopyOverview()
    {
        var text = IsEditingOverview ? (EditedOverview ?? string.Empty) : (AgentOverview ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text)) return;
        Clipboard.SetText(text);
    }

    [RelayCommand]
    private void CopySmeNotes()
    {
        if (string.IsNullOrWhiteSpace(SmeNotes)) return;
        Clipboard.SetText(SmeNotes);
    }

    [RelayCommand]
    private void BackToDashboard()
    {
        _mainViewModel.NavigateToDashboard();
    }
}

