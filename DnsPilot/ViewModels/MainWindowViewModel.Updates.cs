using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.ViewModels;

/// <summary>
/// Updates: Prüfung beim Start und alle 30 Minuten, Hinweis oben im Fenster,
/// Update-Dialog mit Versionshinweisen, „Was ist neu" nach einem Update.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _updateLoopCts;

    /// <summary>Abstand der Hintergrundprüfung; für Tests verkürzbar.</summary>
    internal TimeSpan UpdateCheckInterval { get; set; } = TimeSpan.FromMinutes(AppSettings.UpdateCheckMinutes);

    /// <summary>Gefundenes, nicht übersprungenes Update — oder null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowUpdateCommand))]
    private UpdateCheckResult? _availableUpdate;

    [ObservableProperty]
    private string? _updateNotice;

    /// <summary>
    /// Sieht im Update-Ordner nach. Beim Start öffnet ein Fund gleich den
    /// Update-Dialog; die Prüfung im laufenden Betrieb blendet nur den Hinweis
    /// oben ein — ein Dialog mitten in der Arbeit wäre eine Unterbrechung, um
    /// die niemand gebeten hat.
    /// </summary>
    internal async Task CheckForUpdateAsync(bool atStartup)
    {
        UpdateCheckResult result;
        try
        {
            result = await _updates.CheckForUpdateAsync(force: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Update-Fehler sind nie ein Grund, den Nutzer zu stören.
            Log.Warn(ex, "Update-Prüfung fehlgeschlagen.");
            return;
        }

        if (!result.UpdateAvailable || result.LatestVersion is null)
        {
            AvailableUpdate = null;
            UpdateNotice = null;
            return;
        }

        string version = result.LatestVersion.ToString(3);
        if (version == _settings.Current.SkippedUpdateVersion)
        {
            Log.Debug("Update {0} ist übersprungen — kein Hinweis.", version);
            AvailableUpdate = null;
            UpdateNotice = null;
            return;
        }

        bool isNew = AvailableUpdate?.LatestVersion != result.LatestVersion;
        AvailableUpdate = result;
        UpdateNotice = $"DNS-Pilot {version} ist verfügbar.";
        if (isNew) Log.Info("Update {0} gefunden ({1}).", version, atStartup ? "beim Start" : "Hintergrundprüfung");

        if (atStartup) await ShowUpdateAsync().ConfigureAwait(true);
    }

    private bool CanShowUpdate() => AvailableUpdate is not null;

    /// <summary>Öffnet den Update-Dialog mit Versionshinweisen.</summary>
    [RelayCommand(CanExecute = nameof(CanShowUpdate))]
    private async Task ShowUpdateAsync()
    {
        if (_shell is null || AvailableUpdate is not { } update) return;

        var decision = await _shell.ShowUpdateDialogAsync(update).ConfigureAwait(true);
        if (decision == UpdateDialogResult.Skip) SkipUpdate(update);
    }

    /// <summary>„Diese Version überspringen" — erst die nächste wird wieder gemeldet.</summary>
    internal void SkipUpdate(UpdateCheckResult update)
    {
        if (update.LatestVersion is null) return;

        _settings.Current.SkippedUpdateVersion = update.LatestVersion.ToString(3);
        _settings.Save();
        Log.Info("Update {0} wird übersprungen.", _settings.Current.SkippedUpdateVersion);

        if (AvailableUpdate?.LatestVersion == update.LatestVersion)
        {
            AvailableUpdate = null;
            UpdateNotice = null;
        }
    }

    /// <summary>
    /// Prüft im Hintergrund alle <see cref="UpdateCheckInterval"/>. Läuft im
    /// UI-Kontext (ConfigureAwait(true)), damit die Ergebnisse ohne Umweg in
    /// die Oberfläche dürfen.
    /// </summary>
    internal void StartUpdateLoop()
    {
        _updateLoopCts?.Cancel();
        _updateLoopCts = new CancellationTokenSource();
        _ = RunUpdateLoopAsync(_updateLoopCts.Token);
    }

    private async Task RunUpdateLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(UpdateCheckInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(true))
            {
                if (_settings.Current.CheckUpdatesOnStart) await CheckForUpdateAsync(atStartup: false).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Beenden.
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Hintergrund-Update-Prüfung beendet sich unerwartet.");
        }
    }

    /// <summary>
    /// Ist die laufende Version neuer als die zuletzt gestartete, zeigt DNS-Pilot
    /// einmal, was sich seitdem geändert hat — aus dem eingebauten Changelog,
    /// also auch ohne Netzlaufwerk. Beim allerersten Start nicht.
    /// </summary>
    internal async Task ShowWhatsNewIfUpdatedAsync()
    {
        var current = _updates.CurrentVersion;
        string? last = _settings.Current.LastSeenVersion;

        _settings.Current.LastSeenVersion = current.ToString(3);
        _settings.Save();

        if (_shell is null || !Version.TryParse(last, out var lastVersion) || current <= lastVersion) return;

        string notes = Changelog.Between(Changelog.LoadEmbedded(), lastVersion, current);
        if (notes.Length == 0) return;

        Log.Info("Erster Start nach dem Update {0} → {1}: zeige „Was ist neu“.", lastVersion, current);
        await _shell.ShowWhatsNewAsync(
            $"Neu in DNS-Pilot {current.ToString(3)}",
            $"Seit Version {lastVersion.ToString(3)} hat sich Folgendes geändert.",
            notes).ConfigureAwait(true);
    }
}
