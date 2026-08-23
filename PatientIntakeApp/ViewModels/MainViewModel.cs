using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services;
using PatientIntakeApp.Services.Stores;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using MaterialDesignThemes.Wpf;
using PatientIntakeApp.Data.Entities;
using PatientIntakeApp.Views;

namespace PatientIntakeApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigurationService _configService;
    private readonly IThemeService _themeService;
    private readonly IPresenceStore _presenceStore;
    private readonly IReferralEventStore _referralEventStore;
    private readonly IUserStore _userStore;
    private DispatcherTimer? _sessionTimer;
    private DispatcherTimer? _presenceTimer;
    private DispatcherTimer? _transferToastTimer;
    private DateTime _lastTransferToastCheckUtc = DateTime.UtcNow;
    private bool _transferToastPollInFlight;
    private readonly HashSet<Guid> _seenTransferEventIds = new();
    private const int SessionTimeoutMinutes = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIngestionActive))]
    private ObservableObject? _currentViewModel;

    [ObservableProperty]
    private Facility? _selectedFacility;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedIn))]
    [NotifyPropertyChangedFor(nameof(UsernameDisplay))]
    private AppUser? _currentUser;

    public bool IsLoggedIn => CurrentUser != null;
    public string UsernameDisplay => CurrentUser?.Username ?? string.Empty;
    public bool IsIngestionActive => CurrentViewModel is IngestionViewModel;

    [ObservableProperty]
    private DevSettings _devSettings = new DevSettings();

    private bool _devSettingsPersistenceHooked;

    [ObservableProperty]
    private bool _isSettingsVisible;

    [ObservableProperty]
    private SettingsViewModel? _settings;

    [ObservableProperty]
    private bool _isDialogVisible;

    [ObservableProperty]
    private ObservableObject? _activeDialog;

    public SnackbarMessageQueue ToastQueue { get; } = new(TimeSpan.FromSeconds(4));

    public MainViewModel(
        IServiceProvider serviceProvider,
        IConfigurationService configService,
        IThemeService themeService,
        IPresenceStore presenceStore,
        IReferralEventStore referralEventStore,
        IUserStore userStore)
    {
        _serviceProvider = serviceProvider;
        _configService = configService;
        _themeService = themeService;
        _presenceStore = presenceStore;
        _referralEventStore = referralEventStore;
        _userStore = userStore;
        InitializeSessionTimer();
        InitializePresenceTimer();
        InitializeTransferToastTimer();
    }

    private void InitializeSessionTimer()
    {
        _sessionTimer = new DispatcherTimer();
        _sessionTimer.Interval = TimeSpan.FromMinutes(SessionTimeoutMinutes);
        _sessionTimer.Tick += SessionTimedOut;
        
        // Hook into global input to reset timer
        // In a real app, this would be done via an IMessageFilter or InputManager.
        // For now, we'll simulate "activity" reset on navigation.
    }

    private void InitializePresenceTimer()
    {
        _presenceTimer = new DispatcherTimer();
        _presenceTimer.Interval = TimeSpan.FromSeconds(15);
        _presenceTimer.Tick += (_, __) => _ = HeartbeatAsync();
    }

    private void InitializeTransferToastTimer()
    {
        _transferToastTimer = new DispatcherTimer();
        _transferToastTimer.Interval = TimeSpan.FromSeconds(5);
        _transferToastTimer.Tick += (_, __) => _ = PollTransferToastsAsync();
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            var userId = CurrentUser?.Id ?? Guid.Empty;
            if (userId == Guid.Empty) return;
            await _presenceStore.HeartbeatAsync(userId);
        }
        catch
        {
            // non-fatal
        }
    }

    partial void OnCurrentUserChanged(AppUser? value)
    {
        if (value == null)
        {
            _presenceTimer?.Stop();
            _transferToastTimer?.Stop();
            return;
        }

        _presenceTimer?.Start();
        _ = HeartbeatAsync();

        _seenTransferEventIds.Clear();
        _lastTransferToastCheckUtc = DateTime.UtcNow;
        _transferToastTimer?.Start();
    }

    public void ShowToast(string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            ToastQueue.Enqueue(message.Trim());
        }
        catch
        {
            // non-fatal
        }
    }

    public void ShowToastWithAction(string message, string actionText, Action action)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (string.IsNullOrWhiteSpace(actionText) || action == null)
            {
                ToastQueue.Enqueue(message.Trim());
                return;
            }

            ToastQueue.Enqueue(message.Trim(), actionText.Trim(), () =>
            {
                try { action(); } catch { }
            });
        }
        catch
        {
            // non-fatal
        }
    }

    public void ShowDuplicateReferralsWindow(IEnumerable<ReferralEntity> duplicates, string? title = null)
    {
        try
        {
            var list = (duplicates ?? Array.Empty<ReferralEntity>()).ToList();
            if (list.Count == 0) return;

            ActiveDialog = new DuplicateReferralsViewModel(
                this,
                list,
                close: () =>
                {
                    IsDialogVisible = false;
                    ActiveDialog = null;
                })
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Possible duplicates" : title.Trim()
            };

            IsDialogVisible = true;
        }
        catch
        {
            // non-fatal
        }
    }

    public async Task OpenReferralInNewWindowAsync(Guid referralId)
    {
        if (referralId == Guid.Empty) return;
        if (CurrentUser == null) return;

        try
        {
            var scopeFactory = _serviceProvider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
            var scope = scopeFactory.CreateScope();

            var w = scope.ServiceProvider.GetRequiredService<PatientIntakeApp.Views.MainWindow>();
            var vm = scope.ServiceProvider.GetRequiredService<MainViewModel>();

            vm.Initialize();
            // Carry over login context
            vm.CurrentUser = CurrentUser;
            vm.NavigateToDashboard();

            w.DataContext = vm;
            w.Closed += (_, __) => scope.Dispose();
            w.Show();
            w.Activate();

            await vm.OpenReferralAsync(referralId);
        }
        catch
        {
            // non-fatal
        }
    }

    public async Task OpenReferralAsync(Guid referralId)
    {
        if (referralId == Guid.Empty) return;

        try
        {
            var referralStore = _serviceProvider.GetRequiredService<IReferralStore>();
            var reviewStore = _serviceProvider.GetRequiredService<IReviewStore>();

            var referral = await referralStore.GetByIdAsync(referralId);
            if (referral == null) return;
            if (string.IsNullOrWhiteSpace(referral.SourceFilePath)) return;

            // Align facility selection before processing/review.
            var legacyFacilityId = referral.Facility?.LegacyId;
            if (!string.IsNullOrWhiteSpace(legacyFacilityId))
            {
                var facility = _configService.GetFacilities().FirstOrDefault(f => f.Id == legacyFacilityId);
                if (facility != null)
                {
                    SelectedFacility = facility;
                }
            }

            // Use the same resume behavior as the dashboard Open button.
            if (referral.Status is ReferralStatus.Paused or ReferralStatus.InProgress or ReferralStatus.Completed or ReferralStatus.New)
            {
                var latest = await reviewStore.GetLatestSessionWithFindingsAsync(referral.Id);
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

                        NavigateToFinalReport(referral.SourceFileName, findings, overview, contextViolations, latest.Value.Session.Id);
                        return;
                    }

                    var agent = string.IsNullOrWhiteSpace(overview) ? null : new Models.AgentOverviewResult { Overview = overview };
                    NavigateToReview(findings, referral.SourceFilePath, agent);
                    return;
                }
            }

            NavigateToProcessing(new[] { referral.SourceFilePath });
        }
        catch
        {
            // non-fatal
        }
    }

    private async Task PollTransferToastsAsync()
    {
        var userId = CurrentUser?.Id ?? Guid.Empty;
        if (userId == Guid.Empty) return;
        if (_transferToastPollInFlight) return;

        _transferToastPollInFlight = true;
        try
        {
            var since = _lastTransferToastCheckUtc;
            var events = await _referralEventStore.ListTransfersToUserSinceAsync(userId, since);
            if (events.Count == 0) return;

            // Advance cursor so we don't re-poll the same window.
            _lastTransferToastCheckUtc = events.Max(e => e.AtUtc).AddMilliseconds(1);

            // Resolve user names once.
            var users = await _userStore.ListActiveUsersAsync();
            var byId = users.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName!);

            foreach (var e in events)
            {
                if (_seenTransferEventIds.Contains(e.EventId)) continue;
                _seenTransferEventIds.Add(e.EventId);
                if (_seenTransferEventIds.Count > 500)
                {
                    // keep memory bounded (best-effort)
                    _seenTransferEventIds.Clear();
                }

                var fromName = e.FromUserId.HasValue && byId.TryGetValue(e.FromUserId.Value, out var n) ? n : null;
                var file = string.IsNullOrWhiteSpace(e.SourceFileName) ? "a referral" : e.SourceFileName;
                ShowToast(fromName == null
                    ? $"Referral transferred to you: {file}"
                    : $"Referral transferred to you from {fromName}: {file}");
            }

            // If dashboard is visible, refresh queues so the new assignment appears immediately.
            if (CurrentViewModel is DashboardViewModel d)
            {
                d.RefreshCommand.Execute(null);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _transferToastPollInFlight = false;
        }
    }

    private void SessionTimedOut(object? sender, EventArgs e)
    {
        if (DevSettings.DisableAutoLogout)
        {
            // Developer option: keep the session alive even when idle.
            ResetSessionTimer();
            return;
        }

        _sessionTimer?.Stop();
        _ = ShowInfoAsync("Session Expired", "Session timed out due to inactivity.", iconKind: "ClockAlertOutline");

        // Close any overlays on forced logout.
        DevSettings.IsDevMenuVisible = false;
        IsSettingsVisible = false;

        // Clear sensitive data and go to start
        SelectedFacility = null;
        CurrentUser = null;
        NavigateToLogin();
    }

    private void ResetSessionTimer()
    {
        _sessionTimer?.Stop();
        _sessionTimer?.Start();
    }

    public void Initialize()
    {
        // Apply persisted theme before showing first view.
        _themeService.ApplyDarkMode(_configService.GetDarkModeEnabled());

        // Load persisted developer options (deprecated dev menu; options now live in Settings -> Developer Options).
        DevSettings = _configService.GetDevSettings();
        HookDevSettingsPersistence();

        // Start at Login
        CurrentViewModel = _serviceProvider.GetRequiredService<LoginViewModel>();
        _sessionTimer?.Start();
    }

    private void HookDevSettingsPersistence()
    {
        if (_devSettingsPersistenceHooked) return;
        _devSettingsPersistenceHooked = true;

        DevSettings.PropertyChanged += (_, __) =>
        {
            try
            {
                _configService.SaveDevSettings(DevSettings);
            }
            catch
            {
                // Ignore persistence errors; dev settings are non-critical.
            }
        };
    }

    public void NavigateToLogin()
    {
        ResetSessionTimer();
        CurrentViewModel = _serviceProvider.GetRequiredService<LoginViewModel>();
    }

    public void NavigateToDashboard()
    {
        ResetSessionTimer();
        var vm = _serviceProvider.GetRequiredService<DashboardViewModel>();
        CurrentViewModel = vm;
        _ = vm.InitializeAsync();
    }

    public void NavigateToIngestion()
    {
        ResetSessionTimer();
        CurrentViewModel = _serviceProvider.GetRequiredService<IngestionViewModel>();
    }

    public void NavigateToProcessing(string[] files)
    {
        ResetSessionTimer();
        var vm = _serviceProvider.GetRequiredService<ProcessingViewModel>();
        CurrentViewModel = vm;
        // Fire and forget processing start, handled within VM
        _ = vm.StartProcessingAsync(files);
    }

    public void NavigateToReview(List<Finding> findings, string? pdfPath, AgentOverviewResult? agentOverview = null)
    {
        ResetSessionTimer();
        var vm = _serviceProvider.GetRequiredService<ReviewViewModel>();
        if (pdfPath != null)
        {
            vm.Initialize(findings, pdfPath, agentOverview);
        }
        CurrentViewModel = vm;
    }

    public void NavigateToFinalReport(string? sourceFileName, List<Finding> findings, string? agentOverview, List<Finding> contextViolations, Guid? reviewSessionId = null)
    {
        ResetSessionTimer();
        var vm = _serviceProvider.GetRequiredService<FinalReportViewModel>();
        vm.Initialize(sourceFileName, findings, agentOverview, contextViolations, reviewSessionId);
        CurrentViewModel = vm;
    }

    public void NavigateToAllClear()
    {
        ResetSessionTimer();
        CurrentViewModel = _serviceProvider.GetRequiredService<AllClearViewModel>();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (!IsLoggedIn) return;

        Settings ??= _serviceProvider.GetRequiredService<SettingsViewModel>();
        Settings.Refresh();
        IsSettingsVisible = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsVisible = false;
    }

    [RelayCommand]
    private async Task Logout()
    {
        var confirmed = await ShowConfirmAsync("Confirm Logout", "Are you sure you want to log out?", iconKind: "HelpCircleOutline");
        if (!confirmed) return;

        DevSettings.IsDevMenuVisible = false;
        IsSettingsVisible = false;
        CurrentUser = null;
        NavigateToLogin();
    }

    public async Task<bool> ShowConfirmAsync(string title, string message, string iconKind = "HelpCircleOutline")
    {
        var result = await ShowDialogAsync(title, message, iconKind, DialogButtons.YesNo);
        return result == DialogResult.Yes;
    }

    public Task ShowInfoAsync(string title, string message, string iconKind = "InformationOutline")
        => ShowDialogAsync(title, message, iconKind, DialogButtons.Ok);

    public async Task<DialogResult> ShowDialogAsync(string title, string message, string iconKind, DialogButtons buttons)
    {
        var tcs = new TaskCompletionSource<DialogResult>();

        var parsed = MaterialDesignThemes.Wpf.PackIconKind.HelpCircleOutline;
        if (!Enum.TryParse(iconKind, ignoreCase: true, out parsed))
        {
            parsed = MaterialDesignThemes.Wpf.PackIconKind.HelpCircleOutline;
        }

        ActiveDialog = new DialogViewModel(
            title,
            message,
            parsed,
            buttons,
            result =>
            {
                IsDialogVisible = false;
                ActiveDialog = null;
                tcs.TrySetResult(result);
            });

        IsDialogVisible = true;
        return await tcs.Task;
    }

    public async Task<Guid?> ShowTransferUserDialogAsync(string title, IEnumerable<TransferUserOption> users)
    {
        var tcs = new TaskCompletionSource<Guid?>();

        ActiveDialog = new TransferUserDialogViewModel(
            title,
            users,
            selectedUserId =>
            {
                IsDialogVisible = false;
                ActiveDialog = null;
                tcs.TrySetResult(selectedUserId);
            });

        IsDialogVisible = true;
        return await tcs.Task;
    }

    public async Task<bool> ShowChoiceAsync(string title, string message, string yesText, string noText, string iconKind = "HelpCircleOutline")
    {
        var tcs = new TaskCompletionSource<DialogResult>();

        var parsed = MaterialDesignThemes.Wpf.PackIconKind.HelpCircleOutline;
        if (!Enum.TryParse(iconKind, ignoreCase: true, out parsed))
        {
            parsed = MaterialDesignThemes.Wpf.PackIconKind.HelpCircleOutline;
        }

        ActiveDialog = new DialogViewModel(
            title,
            message,
            parsed,
            DialogButtons.YesNo,
            result =>
            {
                IsDialogVisible = false;
                ActiveDialog = null;
                tcs.TrySetResult(result);
            },
            okText: null,
            yesText: yesText,
            noText: noText);

        IsDialogVisible = true;
        var res = await tcs.Task;
        return res == DialogResult.Yes;
    }
}

