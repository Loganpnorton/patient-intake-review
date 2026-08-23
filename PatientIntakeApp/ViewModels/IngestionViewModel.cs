using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services;
using PatientIntakeApp.Services.Stores;

namespace PatientIntakeApp.ViewModels;

public partial class IngestionViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IPdfProcessingService _pdfService;
    private readonly IConfigurationService _configService;
    private readonly IReferralStore _referralStore;

    [ObservableProperty]
    private List<Facility> _facilities;

    [ObservableProperty]
    private string? _selectedFacilityId;

    [ObservableProperty]
    private Facility? _selectedFacility;

    [ObservableProperty]
    private ObservableCollection<StagedFile> _stagedFiles = new ObservableCollection<StagedFile>();

    [ObservableProperty]
    private List<RecentPatient> _recentHistory;

    [ObservableProperty]
    private bool _isDraggingOver;

    [ObservableProperty]
    private bool _isBatchingOverLimit;

    [ObservableProperty]
    private string _patientFirstName = string.Empty;

    [ObservableProperty]
    private string _patientLastName = string.Empty;

    [ObservableProperty]
    private DateTime? _patientDob;

    [ObservableProperty]
    private string _externalMrn = string.Empty;

    public IngestionViewModel(
        MainViewModel mainViewModel,
        IPdfProcessingService pdfService,
        IConfigurationService configService,
        IReferralStore referralStore)
    {
        _mainViewModel = mainViewModel;
        _pdfService = pdfService;
        _configService = configService;
        _referralStore = referralStore;
        _facilities = _configService.GetFacilities();
        _recentHistory = _configService.GetRecentHistory();

        // Bind selection by facility Id so WPF selection doesn't depend on object reference equality.
        SelectedFacilityId = _mainViewModel.SelectedFacility?.Id ?? _configService.GetLastSelectedFacilityId();

        StagedFiles.CollectionChanged += (_, __) => RefreshBatchingState();
        _mainViewModel.DevSettings.PropertyChanged += (_, __) => RefreshBatchingState();
        RefreshBatchingState();
    }

    [RelayCommand]
    private void ReturnToDashboard()
    {
        _mainViewModel.NavigateToDashboard();
    }

    partial void OnSelectedFacilityIdChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SelectedFacility = null;
            return;
        }

        SelectedFacility = Facilities.FirstOrDefault(f => f.Id == value);
    }

    partial void OnSelectedFacilityChanged(Facility? value)
    {
        if (value != null)
        {
            if (SelectedFacilityId != value.Id)
            {
                SelectedFacilityId = value.Id;
            }
            _configService.SetLastSelectedFacilityId(value.Id);
            _mainViewModel.SelectedFacility = value;
        }
    }

    [RelayCommand]
    private void BrowseFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = false, // Single file upload per user request
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Select Patient File"
        };

        if (dialog.ShowDialog() == true)
        {
            StageFiles(new[] { dialog.FileName });
        }
    }

    [RelayCommand]
    private void FilesDropped(string[] files)
    {
        if (files != null && files.Length > 0)
        {
            // Take only the first file if multiple dropped
            StageFiles(new[] { files[0] });
        }
        IsDraggingOver = false;
    }

    private void StageFiles(string[] files)
    {
        foreach (var file in files)
        {
            if (StagedFiles.Any(f => f.FilePath == file)) continue;

            // Calculate page count (this is fast for local files usually, but ideally async)
            // For better UX we could do this in a background task and update the item
            int pages = _pdfService.GetPageCount(file);
            
            StagedFiles.Add(new StagedFile 
            { 
                FilePath = file, 
                PageCount = pages 
            });

            // Duplicate detection toast should happen at staging time (drop/browse), not only on submit.
            _ = CheckDuplicateOnStageAsync(file);
        }
        StartAnalysisCommand.NotifyCanExecuteChanged();
        RefreshBatchingState();
    }

    private readonly HashSet<string> _dupeToastHashes = new(StringComparer.OrdinalIgnoreCase);

    private async Task CheckDuplicateOnStageAsync(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            var dupes = await _referralStore.FindExistingReferralsByFileHashAsync(filePath);
            if (dupes == null || dupes.Count == 0) return;

            // De-dupe toasts by hash (avoid spamming when users re-stage same file repeatedly).
            var hash = dupes.FirstOrDefault()?.SourceFileHash ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(hash))
            {
                if (_dupeToastHashes.Contains(hash)) return;
                _dupeToastHashes.Add(hash);
                if (_dupeToastHashes.Count > 200) _dupeToastHashes.Clear();
            }

            var msg = $"Duplicate PDF detected ({dupes.Count} existing referral(s)).";
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel.ShowToastWithAction(
                    msg,
                    "VIEW",
                    () => _mainViewModel.ShowDuplicateReferralsWindow(dupes, "Exact duplicate detected"));
            });
        }
        catch
        {
            // non-fatal
        }
    }

    [RelayCommand]
    private void RemoveFile(StagedFile file)
    {
        if (file != null)
        {
            StagedFiles.Remove(file);
            StartAnalysisCommand.NotifyCanExecuteChanged();
            RefreshBatchingState();
        }
    }

    public bool CanStartAnalysis => StagedFiles.Any();

    [RelayCommand(CanExecute = nameof(CanStartAnalysis))]
    private async Task StartAnalysis()
    {
        var filePaths = StagedFiles.Select(f => f.FilePath).ToArray();

        try
        {
            // Create a shared referral record so it appears on the dashboard for other users.
            if (_mainViewModel.CurrentUser != null && !string.IsNullOrWhiteSpace(SelectedFacilityId))
            {
                foreach (var file in filePaths)
                {
                    var req = new CreateReferralRequest(
                        FacilityLegacyId: SelectedFacilityId,
                        SourceFileName: Path.GetFileName(file),
                        SourceFilePath: file,
                        PatientFirstName: string.IsNullOrWhiteSpace(PatientFirstName) ? null : PatientFirstName.Trim(),
                        PatientLastName: string.IsNullOrWhiteSpace(PatientLastName) ? null : PatientLastName.Trim(),
                        PatientDob: PatientDob,
                        ExternalMrn: string.IsNullOrWhiteSpace(ExternalMrn) ? null : ExternalMrn.Trim()
                    );

                    var created = await _referralStore.CreateReferralAsync(req, actorUserId: _mainViewModel.CurrentUser.Id);

                    // Duplicate toast + popout link is handled on stage; avoid double-toast here.

                    // Auto-assign the referral to the current user for immediate work (can be transferred later).
                    await _referralStore.AssignAsync(created.Referral.Id, _mainViewModel.CurrentUser.Id, _mainViewModel.CurrentUser.Id);
                }
            }
        }
        catch (Exception ex)
        {
            await _mainViewModel.ShowInfoAsync("Referral Create Error", $"Could not create referral in shared queue:\n\n{ex.Message}", iconKind: "AlertCircleOutline");
        }

        _mainViewModel.NavigateToProcessing(filePaths);
        
        // Clear staging for next time
        StagedFiles.Clear();
        PatientFirstName = string.Empty;
        PatientLastName = string.Empty;
        PatientDob = null;
        ExternalMrn = string.Empty;
        RefreshBatchingState();
    }

    [RelayCommand]
    private async Task ShowRecentHistoryInfo(RecentPatient patient)
    {
        if (patient == null) return;

        await _mainViewModel.ShowInfoAsync(
            "Reports Deprecated",
            "Final report PDFs are no longer generated or opened from the app.\n\nAfter you click 'Complete Review', you’ll be taken to an in-app report. Use 'Back to Staging Queue' to end the session.",
            iconKind: "InformationOutline");
    }
    
    public void SetDragState(bool isDragging)
    {
        IsDraggingOver = isDragging;
    }

    private void RefreshBatchingState()
    {
        var enabled = _mainViewModel.DevSettings.EnableAiBatching;
        var limit = Math.Max(1, _mainViewModel.DevSettings.AiBatchPageLimit);
        IsBatchingOverLimit = enabled && StagedFiles.Any(f => f.PageCount > limit);
    }

    [RelayCommand]
    private async Task ShowBatchingInfo()
    {
        await _mainViewModel.ShowInfoAsync(
            "Batching Enabled",
            "Batching has been enabled for this PDF document as it is over the page limit. Once each batch has completed you will be prompted to proceed to review if flagged items are found or continue with the next batch.",
            iconKind: "InformationOutline");
    }
}


