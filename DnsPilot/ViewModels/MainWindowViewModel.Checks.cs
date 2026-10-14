using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsPilot.Services;

namespace DnsPilot.ViewModels;

/// <summary>Erreichbarkeit und Auflösung prüfen.</summary>
public sealed partial class MainWindowViewModel
{
    private readonly ScanService _scanner;
    private readonly SightingStore _sightings;
    private CancellationTokenSource? _scanCts;

    /// <summary>
    /// Ab so vielen Einträgen fragt „Sichtbare prüfen" einmal nach. Ein
    /// versehentlicher Klick auf „alle 4.000" soll nicht einfach losrennen.
    /// </summary>
    internal const int LargeScanThreshold = 300;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanVisibleCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateMissingPtrCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string? _scanText;

    /// <summary>Wartet „Sichtbare prüfen" auf die Bestätigung des großen Laufs?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanVisibleLabel))]
    private bool _confirmLargeScan;

    public string ScanVisibleLabel => ConfirmLargeScan
        ? $"Wirklich {VisibleRows.Count:N0} prüfen?"
        : "Sichtbare prüfen";

    partial void OnVisibleRowsChanged(IReadOnlyList<RecordRow> value) => ConfirmLargeScan = false;

    private bool CanScanVisible() => !IsBusy && !IsScanning && VisibleRows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanScanVisible))]
    private async Task ScanVisibleAsync()
    {
        if (VisibleRows.Count > LargeScanThreshold && !ConfirmLargeScan)
        {
            ConfirmLargeScan = true;
            StatusText = $"{VisibleRows.Count:N0} Einträge — bitte noch einmal klicken, um die Prüfung zu starten.";
            return;
        }

        ConfirmLargeScan = false;
        await ScanAsync(VisibleRows).ConfigureAwait(true);
    }

    private bool CanScanSelected() => !IsBusy && !IsScanning && SelectedRows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanScanSelected))]
    private Task ScanSelectedAsync() => ScanAsync(SelectedRows);

    private bool CanCancelScan() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan() => _scanCts?.Cancel();

    internal async Task ScanAsync(IReadOnlyList<RecordRow> rows)
    {
        var targets = rows.Select(r => ScanTarget.For(r.Record)).ToList();
        if (targets.Count == 0) return;

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanProgress = 0;
        ScanText = $"0 / {targets.Count}";
        ErrorMessage = null;

        var cfg = _settings.Current;
        var progress = new Progress<ScanUpdate>(update =>
        {
            if (_rowsByKey.TryGetValue(update.Key, out var row))
            {
                if (update.Sighting is not null)
                {
                    row.ApplySighting(update.Sighting, DateTime.UtcNow, cfg.CandidateAfterDays, cfg.CandidateMinChecks);
                }
                row.ApplyResolve(update.Resolve);
            }

            ScanProgress = 100.0 * update.Done / update.Total;
            ScanText = $"{update.Done} / {update.Total}";
        });

        try
        {
            await _scanner.ScanAsync(targets, progress, _scanCts.Token).ConfigureAwait(true);
            StatusText = $"{targets.Count} Einträge geprüft.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Prüfung abgebrochen — die bisherigen Ergebnisse sind gespeichert.";
        }
        catch (Exception ex)
        {
            Fail("Die Prüfung ist fehlgeschlagen. Details stehen im Log.", ex);
        }
        finally
        {
            IsScanning = false;
            _scanCts.Dispose();
            _scanCts = null;

            // Kandidaten und Auflösungsprobleme sind erst jetzt bekannt.
            if (OnlyProblems) ApplyFilter();
            RefreshSummaryCounts();
        }
    }

    private void RefreshSummaryCounts()
    {
        int candidates = _allRows.Count(r => r.IsCandidate);
        int online = _allRows.Count(r => r.IsOnline);
        ScanText = $"{online:N0} online · {candidates:N0} Kandidaten";
    }
}
