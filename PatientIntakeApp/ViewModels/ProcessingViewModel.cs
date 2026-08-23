using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PatientIntakeApp.Models;
using PatientIntakeApp.Services;

namespace PatientIntakeApp.ViewModels;

public partial class ProcessingViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly IPdfProcessingService _pdfService;
    private readonly IAnalysisService _analysisService;

    [ObservableProperty]
    private string _statusText = "Initializing...";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private ObservableCollection<string> _filesBeingProcessed = new ObservableCollection<string>();

    private bool _isProcessing;

    public ProcessingViewModel(MainViewModel mainViewModel, IPdfProcessingService pdfService, IAnalysisService analysisService)
    {
        _mainViewModel = mainViewModel;
        _pdfService = pdfService;
        _analysisService = analysisService;
    }

    public async Task StartProcessingAsync(string[] files)
    {
        // Boolean gate: prevent re-entry from accidental double-clicks or UI re-triggers.
        if (_isProcessing)
        {
            Log("[ProcessingViewModel] StartProcessingAsync skipped: already processing.");
            return;
        }
        _isProcessing = true;

        try
        {
            FilesBeingProcessed.Clear();
            foreach (var f in files) FilesBeingProcessed.Add(System.IO.Path.GetFileName(f));

            var facility = _mainViewModel.SelectedFacility;
            if (facility == null)
            {
                StatusText = "Error: No Facility Selected.";
                return;
            }

            var allFindings = new List<Finding>();
            string? lastPdfPath = null;
            AgentOverviewResult? lastAgentOverview = null;

            int totalFiles = files.Length;
            for (int i = 0; i < totalFiles; i++)
            {
                var file = files[i];
                lastPdfPath = file;

                UpdateStatus($"Processing file {i + 1} of {totalFiles}: {System.IO.Path.GetFileName(file)}", (double)i / totalFiles * 100);

                // Extract (sync PDF work -> background)
                UpdateStatus($"Extracting text from {System.IO.Path.GetFileName(file)}...", (double)i / totalFiles * 100);
                var pages = await Task.Run(() => _pdfService.ExtractText(file));

                var findingsForThisFile = new List<Finding>();

                var batchLimit = Math.Max(1, _mainViewModel.DevSettings.AiBatchPageLimit);
                var shouldBatchInteractively = _mainViewModel.DevSettings.EnableAiBatching && pages.Count > batchLimit;

                if (shouldBatchInteractively)
                {
                    // Interactive batching: process in user-controlled page groups with prompt-per-batch.
                    var analyzedPages = new List<int>();
                    for (var start = 0; start < pages.Count; start += batchLimit)
                    {
                        var batchPages = pages.Skip(start).Take(batchLimit).ToList();
                        foreach (var pg in batchPages) analyzedPages.Add(pg.PageNumber);

                        var batchLabel = $"{batchPages.First().PageNumber}-{batchPages.Last().PageNumber}";
                        UpdateStatus($"Analyzing pages {batchLabel}...", (double)i / totalFiles * 100);
                        var batchFindings = await _analysisService.AnalyzeDocumentAsync(batchPages, facility, _mainViewModel.DevSettings);
                        findingsForThisFile.AddRange(batchFindings);

                        if (batchFindings.Any())
                        {
                            var continueAnalyzing = await System.Windows.Application.Current.Dispatcher
                                .InvokeAsync(() =>
                                    _mainViewModel.ShowChoiceAsync(
                                        "Flags found",
                                        $"We've found flagged items for pages {batchLabel}.",
                                        yesText: "Continue analyzing",
                                        noText: "Review now",
                                        iconKind: "FlagOutline"))
                                .Task
                                .Unwrap();

                            if (!continueAnalyzing)
                            {
                                var subsetPdf = _pdfService.CreateSubsetPdf(file, analyzedPages);
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    _mainViewModel.NavigateToReview(findingsForThisFile, subsetPdf, null);
                                });
                                return;
                            }
                        }
                    }
                }
                else
                {
                    // PRIMARY PATH: Single batched API call - sends ALL pages + findings prompt + overview prompt in ONE request.
                    // This avoids the RPM limit violation (was: 10 per-page calls + 1 overview call = 11 requests).
                    UpdateStatus($"Analyzing content (batched AI)...", (double)i / totalFiles * 100);
                    var batchResult = await _analysisService.AnalyzeDocumentBatchAsync(pages, facility, _mainViewModel.DevSettings);
                    findingsForThisFile = batchResult.Findings;
                    lastAgentOverview = batchResult.AgentOverview;
                }

                allFindings.AddRange(findingsForThisFile);
            }

            UpdateStatus("Processing Complete", 100);

            if (allFindings.Any())
            {
                _mainViewModel.NavigateToReview(allFindings, lastPdfPath, lastAgentOverview);
            }
            else
            {
                _mainViewModel.NavigateToAllClear();
            }
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText("debug_log.txt", $"{DateTime.Now}: {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void UpdateStatus(string text, double progress)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = text;
            ProgressValue = progress;
        });
    }
}


