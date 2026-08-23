using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using PatientIntakeApp.Services.Stores;
using System.Threading;

namespace PatientIntakeApp.ViewModels;

public partial class ReviewViewModel : ObservableObject
{
    public enum ReviewQueueTab
    {
        NeedsReview = 0,
        Reviewed = 1
    }

    private readonly MainViewModel _mainViewModel;

    private readonly IPdfProcessingService _pdfService;
    private readonly IConfigurationService _configService;
    private readonly IReferralStore _referralStore;
    private readonly IReviewStore _reviewStore;

    private string? _sourcePdfPath;
    private AgentOverviewResult? _agentOverview;
    private Guid _referralId;
    private Guid _reviewSessionId;
    private CancellationTokenSource? _persistCts;
    private readonly SemaphoreSlim _persistGate = new SemaphoreSlim(1, 1);

    [ObservableProperty]
    private string _pdfSource = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Finding> _findings = new ObservableCollection<Finding>();

    [ObservableProperty]
    private FindingGroup? _selectedFinding;

    [ObservableProperty]
    private ObservableCollection<FindingGroup> _filteredFindings = new ObservableCollection<FindingGroup>();

    [ObservableProperty]
    private string _selectedFilter = "All";

    [ObservableProperty]
    private ReviewQueueTab _selectedQueueTab = ReviewQueueTab.NeedsReview;

    [ObservableProperty]
    private bool _isReviewPaused;

    [ObservableProperty]
    private string _pauseReason = string.Empty;

    [ObservableProperty]
    private string _smeNotes = string.Empty;

    public ReviewViewModel(
        MainViewModel mainViewModel,
        IPdfProcessingService pdfService,
        IConfigurationService configService,
        IReferralStore referralStore,
        IReviewStore reviewStore)
    {
        _mainViewModel = mainViewModel;
        _pdfService = pdfService;
        _configService = configService;
        _referralStore = referralStore;
        _reviewStore = reviewStore;
    }

    // Counts are based on grouped cards, not raw findings.
    public int NeedsReviewCount => _groups.Count(g => !g.IsReviewed);
    public int ReviewedCount => _groups.Count(g => g.IsReviewed);
    public string NeedsReviewTabLabel => $"Needs Review: {NeedsReviewCount}";
    public string ReviewedTabLabel => $"Reviewed: {ReviewedCount}";

    public void Initialize(List<Finding> findings, string pdfPath, AgentOverviewResult? agentOverview)
    {
        _ = InitializeAsync(findings, pdfPath, agentOverview);
    }

    private async Task InitializeAsync(List<Finding> findings, string pdfPath, AgentOverviewResult? agentOverview)
    {
        System.Diagnostics.Debug.WriteLine($"[ReviewViewModel] Initializing review for: {pdfPath}");
        _sourcePdfPath = pdfPath;
        _agentOverview = agentOverview;
        Findings = new ObservableCollection<Finding>(findings);
        _referralId = Guid.Empty;
        _reviewSessionId = Guid.Empty;
        IsReviewPaused = false;
        PauseReason = string.Empty;
        SmeNotes = string.Empty;

        // Inject agent-level context rule violations into the review queue so they can be reviewed like other findings.
        try
        {
            var facilityIdForContext = _mainViewModel.SelectedFacility?.Id;
            var facilityForContext = _configService.GetFacilities().FirstOrDefault(f => f.Id == facilityIdForContext);
            InjectAgentContextViolationsIntoFindings(facilityForContext);
        }
        catch
        {
            // Non-critical
        }

        HookFindingsEvents();
        NotifyTabCountsChanged();
        RebuildGroups();
        ApplyFilter(); // Initialize filtered list

        await TryStartOrResumeDbReviewSessionAsync();
        SchedulePersist();
        
        // Highlight terms in the PDF
        // We collect all terms from the findings AND the facility rules (since user wants "all flagged words")
        var facilityId = _mainViewModel.SelectedFacility?.Id;
        var facility = _configService.GetFacilities().FirstOrDefault(f => f.Id == facilityId);
        
        var termsToHighlight = new List<string>();
        
        // Add rule keywords
        if (facility != null) 
        {
            termsToHighlight.AddRange(facility.Rules);
            System.Diagnostics.Debug.WriteLine($"[ReviewViewModel] Added facility rules to highlight: {string.Join(", ", facility.Rules)}");
        }
        
        // Add found terms
        // Don't try to highlight context-rule "rule text" on the PDF (it usually won't appear verbatim).
        var foundTerms = Findings
            .Where(f => !string.Equals(f.Category, "Context Rule", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Term)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        termsToHighlight.AddRange(foundTerms);
        System.Diagnostics.Debug.WriteLine($"[ReviewViewModel] Added found terms to highlight: {string.Join(", ", foundTerms)}");
        
        // Context evidence phrases highlighted in purple (page-scoped)
        var purpleTermsByPage = Findings
            .Where(f => string.Equals(f.Category, "Context Rule", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.Page)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(f => (f.Context ?? "").Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => (s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? "").Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var distinctTerms = termsToHighlight.Distinct().ToList();
        System.Diagnostics.Debug.WriteLine($"[ReviewViewModel] Highlighting {distinctTerms.Count} distinct terms.");
        
        var highlightedPath = _pdfService.HighlightTerms(pdfPath, distinctTerms, purpleTermsByPage);
        System.Diagnostics.Debug.WriteLine($"[ReviewViewModel] Highlighted PDF saved to: {highlightedPath}");

        // Ensure valid file URI for WebView2
        try
        {
            PdfSource = new Uri(highlightedPath).AbsoluteUri;
        }
        catch
        {
            PdfSource = highlightedPath;
        }
    }

    private async Task TryStartOrResumeDbReviewSessionAsync()
    {
        try
        {
            if (_mainViewModel.CurrentUser == null) return;
            if (string.IsNullOrWhiteSpace(_sourcePdfPath)) return;

            var referral = await _referralStore.GetBySourceFilePathAsync(_sourcePdfPath);
            if (referral == null) return;

            _referralId = referral.Id;

            var latest = await _reviewStore.GetLatestSessionWithFindingsAsync(referral.Id);
            if (latest != null && latest.Value.Session.State != PatientIntakeApp.Data.Entities.ReviewSessionState.Completed)
            {
                _reviewSessionId = latest.Value.Session.Id;
                IsReviewPaused = latest.Value.Session.State == PatientIntakeApp.Data.Entities.ReviewSessionState.Paused;
                PauseReason = latest.Value.Session.PauseReason ?? string.Empty;
                SmeNotes = latest.Value.Session.SmeNotes ?? string.Empty;

                // If processing produced a narrative but the session never stored it (e.g. transient AI errors earlier),
                // persist it once (best-effort) so the Final Report can show it later without re-running AI.
                if (!string.IsNullOrWhiteSpace(_agentOverview?.Overview))
                {
                    _ = _reviewStore.SaveSessionRawOverviewIfEmptyAsync(_reviewSessionId, _agentOverview!.Overview);
                }
                return;
            }

            var session = await _reviewStore.CreateSessionAsync(referral.Id, _mainViewModel.CurrentUser.Id, _agentOverview?.Overview);
            _reviewSessionId = session.Id;
        }
        catch
        {
            // Non-fatal: app can still function without DB persistence in a pinch.
        }
    }

    partial void OnSmeNotesChanged(string value)
    {
        SchedulePersist();
    }

    private async Task PersistNowAsync()
    {
        if (_reviewSessionId == Guid.Empty) return;

        await _persistGate.WaitAsync();
        try
        {
            await _reviewStore.SaveFindingsSnapshotAsync(_reviewSessionId, Findings.ToList());
            await _reviewStore.SaveSessionNarrativeAsync(_reviewSessionId, aiOverviewEdited: null, smeNotes: SmeNotes);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private void SchedulePersist()
    {
        if (_reviewSessionId == Guid.Empty) return;

        _persistCts?.Cancel();
        _persistCts = new CancellationTokenSource();
        var token = _persistCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(750, token);
                await _persistGate.WaitAsync(token);
                try
                {
                    await _reviewStore.SaveFindingsSnapshotAsync(_reviewSessionId, Findings.ToList());
                    await _reviewStore.SaveSessionNarrativeAsync(_reviewSessionId, aiOverviewEdited: null, smeNotes: SmeNotes);
                }
                finally
                {
                    _persistGate.Release();
                }
            }
            catch
            {
                // ignore
            }
        }, token);
    }

    private void InjectAgentContextViolationsIntoFindings(Facility? facility)
    {
        if (_agentOverview?.ContextRuleViolations == null || _agentOverview.ContextRuleViolations.Count == 0) return;
        if (facility == null) return;

        var contextSet = new HashSet<string>((facility.ContextRules ?? new List<string>()).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        if (contextSet.Count == 0) return;

        foreach (var v in _agentOverview.ContextRuleViolations)
        {
            var rule = (v.Rule ?? string.Empty).Trim();
            if (v.RuleIndex.HasValue && v.RuleIndex.Value >= 1 && v.RuleIndex.Value <= (facility.ContextRules?.Count ?? 0))
            {
                rule = facility.ContextRules![v.RuleIndex.Value - 1].Trim();
            }
            if (string.IsNullOrWhiteSpace(rule)) continue;
            if (!contextSet.Contains(rule)) continue;

            // Avoid duplicates if already present.
            var page = v.Page ?? 1;
            if (Findings.Any(f =>
                    f.Source == FindingSource.AI &&
                    string.Equals(f.Category, "Context Rule", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Term?.Trim(), rule, StringComparison.OrdinalIgnoreCase) &&
                    f.Page == page))
            {
                continue;
            }

            Findings.Add(new Finding
            {
                Term = rule,
                Category = "Context Rule",
                Page = page,
                Context = string.IsNullOrWhiteSpace(v.Evidence) ? "Detected context rule violation." : v.Evidence.Trim(),
                IsReviewed = false,
                ReviewStatus = ReviewStatus.Pending,
                Source = FindingSource.AI
            });
        }
    }

    private void HookFindingsEvents()
    {
        Findings.CollectionChanged -= Findings_CollectionChanged;
        Findings.CollectionChanged += Findings_CollectionChanged;

        foreach (var f in Findings)
        {
            f.PropertyChanged -= Finding_PropertyChanged;
            f.PropertyChanged += Finding_PropertyChanged;
        }
    }

    private void Findings_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<Finding>())
            {
                item.PropertyChanged -= Finding_PropertyChanged;
                item.PropertyChanged += Finding_PropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<Finding>())
            {
                item.PropertyChanged -= Finding_PropertyChanged;
            }
        }

        NotifyTabCountsChanged();
        RebuildGroups();
    }

    private void Finding_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Finding.IsReviewed) || e.PropertyName == nameof(Finding.ReviewStatus))
        {
            NotifyTabCountsChanged();
            RebuildGroups();
            SchedulePersist();
        }
    }

    private List<FindingGroup> _groups = new();

    private void RebuildGroups()
    {
        // Group by (normalized term, page, matchIndex) so multiple occurrences on the same page don't get lumped.
        _groups = Findings
            .Where(f => !string.IsNullOrWhiteSpace(f.Term))
            .GroupBy(f => (Term: f.Term.Trim().ToLowerInvariant(), f.Page, MatchIndex: f.MatchIndex))
            .Select(g =>
            {
                var ordered = g.OrderBy(f => f.Source).ToList();
                return new FindingGroup
                {
                    Term = ordered.First().Term,
                    Page = g.Key.Page,
                    MatchIndex = g.Key.MatchIndex,
                    Findings = ordered
                };
            })
            // Tiered sorting: Red first, then Yellow, then Green.
            .OrderByDescending(g => g.Severity)
            .ThenBy(g => g.Page)
            .ThenBy(g => g.MatchIndex ?? int.MaxValue)
            .ThenBy(g => g.Term, StringComparer.OrdinalIgnoreCase)
            .ToList();

        NotifyTabCountsChanged();
    }

    private void NotifyTabCountsChanged()
    {
        OnPropertyChanged(nameof(NeedsReviewCount));
        OnPropertyChanged(nameof(ReviewedCount));
        OnPropertyChanged(nameof(NeedsReviewTabLabel));
        OnPropertyChanged(nameof(ReviewedTabLabel));
    }

    [RelayCommand]
    private async Task JumpToFinding(FindingGroup finding)
    {
        if (finding != null && !string.IsNullOrEmpty(PdfSource))
        {
            System.Diagnostics.Debug.WriteLine($"[ReviewViewModel] Jumping to finding on page {finding.Page}");
            
            var cleanSource = PdfSource.Split('#')[0];
            var newSource = $"{cleanSource}#page={finding.Page}";

            // If the source string is effectively the same (only hash changed), 
            // WebView2 might not reload. We force it by clearing and resetting 
            // or simply by notifying change if the UI binds to it.
            
            var timestamp = DateTime.Now.Ticks;
            
            if (PdfSource == newSource)
            {
                // If exactly same, we need to nudge it.
                PdfSource = string.Empty;
                await Task.Delay(50); // UI thread yield
                PdfSource = newSource;
            }
            else
            {
                PdfSource = newSource;
            }
        }
    }

    partial void OnSelectedFindingChanged(FindingGroup? value)
    {
        // Removed single click navigation per user request.
        // Navigation is now handled solely by the double-click command (JumpToFindingCommand).
    }

    [RelayCommand]
    private void Filter(string category)
    {
        SelectedFilter = category;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetQueueTab(string tab)
    {
        SelectedQueueTab = tab?.Equals("Reviewed", StringComparison.OrdinalIgnoreCase) == true
            ? ReviewQueueTab.Reviewed
            : ReviewQueueTab.NeedsReview;

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<FindingGroup> query = _groups;

        // Level 1: Inbox/History tabs
        query = SelectedQueueTab == ReviewQueueTab.NeedsReview
            ? query.Where(g => !g.IsReviewed)
            : query.Where(g => g.IsReviewed);

        // Level 2: Pills
        if (!string.Equals(SelectedFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = SelectedFilter switch
            {
                "AI" => query.Where(g => g.HasAI),
                "Local Flag" => query.Where(g => g.HasLocal),
                _ => query
            };
        }

        FilteredFindings = new ObservableCollection<FindingGroup>(query);

        // Keep selection sane when items move between tabs (e.g., Clear/Flag in Needs Review)
        if (SelectedFinding != null && !FilteredFindings.Contains(SelectedFinding))
        {
            SelectedFinding = FilteredFindings.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void ConfirmFinding(FindingGroup finding)
    {
        if (IsReviewPaused) return;
        if (finding != null)
        {
            foreach (var f in finding.Findings)
            {
                f.IsReviewed = true;
                f.ReviewStatus = ReviewStatus.Passed;
            }
            FinishReviewCommand.NotifyCanExecuteChanged();
            ApplyFilter(); // immediately move it out of "Needs Review"
            NotifyTabCountsChanged();
        }
    }

    [RelayCommand]
    private void DismissFinding(FindingGroup finding)
    {
        if (IsReviewPaused) return;
        if (finding != null)
        {
            foreach (var f in finding.Findings)
            {
                f.IsReviewed = true;
                f.ReviewStatus = ReviewStatus.Rejected;
            }
            FinishReviewCommand.NotifyCanExecuteChanged();
            ApplyFilter(); // immediately move it out of "Needs Review"
            NotifyTabCountsChanged();
        }
    }

    [RelayCommand]
    private void UndoFinding(FindingGroup finding)
    {
        if (IsReviewPaused) return;
        if (finding != null)
        {
            foreach (var f in finding.Findings)
            {
                f.IsReviewed = false;
                f.ReviewStatus = ReviewStatus.Pending;
            }
            FinishReviewCommand.NotifyCanExecuteChanged();
            ApplyFilter(); // immediately move it out of "Reviewed"
            NotifyTabCountsChanged();
        }
    }

    private bool CanFinishReview()
    {
        // Can finish only if all findings are reviewed
        return !IsReviewPaused && Findings.All(f => f.IsReviewed);
    }

    [RelayCommand]
    private async Task PauseReview()
    {
        if (_reviewSessionId == Guid.Empty) return;
        if (_mainViewModel.CurrentUser == null) return;

        IsReviewPaused = true;
        var reason = string.IsNullOrWhiteSpace(PauseReason) ? "Paused for SME consultation" : PauseReason.Trim();
        PauseReason = reason;

        _persistCts?.Cancel(); // stop any pending autosave from racing this transition
        await PersistNowAsync();
        await _reviewStore.SetSessionPausedAsync(_reviewSessionId, paused: true, pauseReason: reason, actorUserId: _mainViewModel.CurrentUser.Id);
        FinishReviewCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ResumeReview()
    {
        if (_reviewSessionId == Guid.Empty) return;
        if (_mainViewModel.CurrentUser == null) return;

        IsReviewPaused = false;
        await _reviewStore.SetSessionPausedAsync(_reviewSessionId, paused: false, pauseReason: null, actorUserId: _mainViewModel.CurrentUser.Id);
        FinishReviewCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanFinishReview))]
    private async Task FinishReview()
    {
        System.Diagnostics.Debug.WriteLine("[ReviewViewModel] Finishing review...");

        try
        {
            // Persist review + mark complete in shared DB (best effort).
            if (_reviewSessionId != Guid.Empty && _mainViewModel.CurrentUser != null)
            {
                _persistCts?.Cancel(); // stop any pending autosave from racing this transition
                await PersistNowAsync();
                await _reviewStore.CompleteSessionAsync(_reviewSessionId, _mainViewModel.CurrentUser.Id);
            }

            if (!string.IsNullOrWhiteSpace(_sourcePdfPath))
            {
                // Context rule section (AI-detected context rule findings)
                var facilityId = _mainViewModel.SelectedFacility?.Id;
                var facility = _configService.GetFacilities().FirstOrDefault(f => f.Id == facilityId);
                var contextSet = new HashSet<string>((facility?.ContextRules ?? new List<string>()).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);

                var contextFindings = Findings
                    .Where(f => f.Source == FindingSource.AI)
                    .Where(f => !string.IsNullOrWhiteSpace(f.Term))
                    .Where(f => contextSet.Contains(f.Term.Trim()))
                    .Where(f => !f.IsFalseFlag)
                    .ToList();

                // De-dupe by (term, page)
                contextFindings = contextFindings
                    .GroupBy(f => (Term: f.Term.Trim().ToLowerInvariant(), f.Page))
                    .Select(g => g.First())
                    .ToList();

                // New behavior: do NOT generate/open/store a PDF report. Navigate straight to the in-app report.
                _mainViewModel.NavigateToFinalReport(
                    Path.GetFileName(_sourcePdfPath),
                    Findings.ToList(),
                    _agentOverview?.Overview,
                    contextFindings,
                    _reviewSessionId == Guid.Empty ? null : _reviewSessionId);

                return;
            }
        }
        catch (Exception ex)
        {
            await _mainViewModel.ShowInfoAsync("Report Error", $"Could not open the in-app report: {ex.Message}", iconKind: "AlertCircleOutline");
        }

        // Fallback: return user to staging/drag-drop screen to start the next patient, keeping facility selection.
        _mainViewModel.NavigateToIngestion();
    }

    [RelayCommand]
    private void ReturnToDashboard()
    {
        // When paused, user wants to bail out to the main page.
        // Any state should already be persisted by PauseReview().
        _persistCts?.Cancel();
        _mainViewModel.NavigateToDashboard();
    }
}
