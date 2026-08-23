using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatientIntakeApp.Data.Entities;
using PatientIntakeApp.Services;
using PatientIntakeApp.Services.ExternalChecks;
using PatientIntakeApp.Services.Stores;
using System.Collections.ObjectModel;
using System.Linq;

namespace PatientIntakeApp.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IReferralStore _referralStore;
    private readonly IUserStore _userStore;
    private readonly IConfigurationService _configService;
    private readonly IReviewStore _reviewStore;
    private readonly IPresenceStore _presenceStore;
    private readonly IExternalCheckService _externalCheckService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ReferralEntity> _unassigned = new();

    [ObservableProperty]
    private ObservableCollection<ReferralEntity> _myAssigned = new();

    [ObservableProperty]
    private ObservableCollection<UserEntity> _activeUsers = new();

    public List<string> StatusFilters { get; } = new()
    {
        "All",
        nameof(ReferralStatus.New),
        nameof(ReferralStatus.InProgress),
        nameof(ReferralStatus.Paused),
        nameof(ReferralStatus.NeedsSme),
        nameof(ReferralStatus.Completed),
        nameof(ReferralStatus.Rejected)
    };

    [ObservableProperty]
    private string _selectedStatusFilter = "All";

    partial void OnSelectedStatusFilterChanged(string value)
    {
        _ = RefreshAsync();
    }

    public DashboardViewModel(
        MainViewModel mainViewModel,
        IReferralStore referralStore,
        IUserStore userStore,
        IConfigurationService configService,
        IReviewStore reviewStore,
        IPresenceStore presenceStore,
        IExternalCheckService externalCheckService)
    {
        _mainViewModel = mainViewModel;
        _referralStore = referralStore;
        _userStore = userStore;
        _configService = configService;
        _reviewStore = reviewStore;
        _presenceStore = presenceStore;
        _externalCheckService = externalCheckService;
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;

            var users = await _userStore.ListActiveUsersAsync();
            ActiveUsers = new ObservableCollection<UserEntity>(users);

            ReferralStatus? status = null;
            if (!string.Equals(SelectedStatusFilter, "All", StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<ReferralStatus>(SelectedStatusFilter, ignoreCase: true, out var parsed))
            {
                status = parsed;
            }

            var all = await _referralStore.ListQueueAsync(status: status, assigneeUserId: null);
            var my = _mainViewModel.CurrentUser?.Id != Guid.Empty
                ? await _referralStore.ListQueueAsync(status: status, assigneeUserId: _mainViewModel.CurrentUser!.Id)
                : new List<ReferralEntity>();

            Unassigned = new ObservableCollection<ReferralEntity>(all.Where(r => r.CurrentAssigneeUserId == null));
            MyAssigned = new ObservableCollection<ReferralEntity>(my);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void NewReferral()
    {
        _mainViewModel.NavigateToIngestion();
    }

    [RelayCommand]
    private async Task AssignToMe(ReferralEntity referral)
    {
        if (referral == null) return;
        if (_mainViewModel.CurrentUser == null) return;

        await _referralStore.AssignAsync(referral.Id, _mainViewModel.CurrentUser.Id, _mainViewModel.CurrentUser.Id);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task Unassign(ReferralEntity referral)
    {
        if (referral == null) return;
        if (_mainViewModel.CurrentUser == null) return;

        await _referralStore.AssignAsync(referral.Id, null, _mainViewModel.CurrentUser.Id);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task Transfer(ReferralEntity referral)
    {
        if (referral == null) return;
        if (_mainViewModel.CurrentUser == null) return;

        var users = await _userStore.ListActiveUsersAsync();
        var online = await _presenceStore.GetOnlineUserIdsAsync(TimeSpan.FromMinutes(2));

        var options = users
            .Where(u => u.Id != _mainViewModel.CurrentUser.Id)
            .Select(u => new TransferUserOption
            {
                UserId = u.Id,
                Username = u.Username,
                DisplayName = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName!,
                IsOnline = online.Contains(u.Id)
            })
            .OrderByDescending(o => o.IsOnline)
            .ThenBy(o => o.DisplayName)
            .ToList();

        var selected = await _mainViewModel.ShowTransferUserDialogAsync("Transfer referral to:", options);
        if (!selected.HasValue) return;

        await _referralStore.AssignAsync(referral.Id, selected.Value, _mainViewModel.CurrentUser.Id);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task Delete(ReferralEntity referral)
    {
        if (referral == null) return;
        if (_mainViewModel.CurrentUser == null) return;

        var confirmed = await _mainViewModel.ShowConfirmAsync(
            "Delete Referral",
            "Deleting a referral removes it permanently and cannot be undone.",
            iconKind: "AlertCircleOutline");

        if (!confirmed) return;

        await _referralStore.DeleteAsync(referral.Id, _mainViewModel.CurrentUser.Id);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RunPreChecks(ReferralEntity referral)
    {
        if (referral == null) return;
        if (_mainViewModel.CurrentUser == null) return;

        await _externalCheckService.RequestChecksAsync(
            referral.Id,
            new[] { ExternalCheckType.Financial, ExternalCheckType.Litigation, ExternalCheckType.Criminal },
            actorUserId: _mainViewModel.CurrentUser.Id);

        await _mainViewModel.ShowInfoAsync("Checks queued", "Financial, litigation, and criminal checks were queued (stub providers).", iconKind: "InformationOutline");
    }

    [RelayCommand]
    private async Task Open(ReferralEntity referral)
    {
        if (referral == null) return;
        if (string.IsNullOrWhiteSpace(referral.SourceFilePath)) return;

        // Ensure facility selection aligns with the referral before processing.
        // We still use config.json facilities until rules are migrated to DB.
        var legacyFacilityId = referral.Facility?.LegacyId;
        if (!string.IsNullOrWhiteSpace(legacyFacilityId))
        {
            var facility = _configService.GetFacilities().FirstOrDefault(f => f.Id == legacyFacilityId);
            if (facility != null)
            {
                _mainViewModel.SelectedFacility = facility;
            }
        }

        // If we have ANY saved session/findings, always resume (never re-run AI analysis).
        try
        {
            var latest = await _reviewStore.GetLatestSessionWithFindingsAsync(referral.Id);
            if (latest != null && latest.Value.Findings.Count > 0)
            {
                var findings = latest.Value.Findings.Select(fe => new Models.Finding
                {
                    Term = fe.Term,
                    Category = fe.Category,
                    Page = fe.Page,
                    Context = fe.Context ?? string.Empty,
                    IsReviewed = fe.IsReviewed,
                    ReviewStatus = fe.ReviewStatus,
                    Severity = fe.Severity,
                    IsFalseFlag = fe.IsFalseFlag,
                    FalseFlagReason = fe.FalseFlagReason,
                    Source = fe.Source,
                    MatchIndex = fe.MatchIndex
                }).ToList();

                var overview = latest.Value.Session.AiOverviewEdited ?? latest.Value.Session.AiOverviewRaw ?? string.Empty;

                if (latest.Value.Session.State == ReviewSessionState.Completed)
                {
                    var contextViolations = findings
                        .Where(f => string.Equals(f.Category, "Context Rule", StringComparison.OrdinalIgnoreCase))
                        .Where(f => !f.IsFalseFlag)
                        .GroupBy(f => (Term: (f.Term ?? "").Trim().ToLowerInvariant(), f.Page))
                        .Select(g => g.First())
                        .ToList();

                    _mainViewModel.NavigateToFinalReport(
                        referral.SourceFileName,
                        findings,
                        overview,
                        contextViolations,
                        latest.Value.Session.Id);
                    return;
                }

                var agent = string.IsNullOrWhiteSpace(overview) ? null : new Models.AgentOverviewResult { Overview = overview };
                _mainViewModel.NavigateToReview(findings, referral.SourceFilePath, agent);
                return;
            }
        }
        catch
        {
            // Fall through to processing
        }

        _mainViewModel.NavigateToProcessing(new[] { referral.SourceFilePath });
    }
}

