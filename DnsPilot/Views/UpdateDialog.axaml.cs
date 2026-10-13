using System.Runtime.Versioning;
using Avalonia.Interactivity;
using DnsPilot.Services;
using NLog;

namespace DnsPilot.Views;

/// <summary>
/// Update-Dialog mit Versionshinweisen — Aufbau wie in Checkmk Cockpit:
/// was sich ändert, dann „Überspringen", „Später" oder „Jetzt installieren".
/// Installiert wird nie ungefragt.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class UpdateDialog : ChromeWindow
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly UpdateService? _updates;
    private readonly UpdateCheckResult? _check;

    public UpdateDialogResult Result { get; private set; } = UpdateDialogResult.Later;

    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public UpdateDialog()
    {
        InitializeComponent();
    }

    public UpdateDialog(UpdateService updates, UpdateCheckResult check) : this()
    {
        _updates = updates;
        _check = check;

        Headline.Text = $"DNS-Pilot {check.LatestVersion?.ToString(3)} ist da";
        SubText.Text = $"Installiert ist {updates.CurrentVersion.ToString(3)}. Das Update ersetzt die Programmdateien "
                       + "und startet DNS-Pilot neu; Einstellungen und Prüfhistorie bleiben.";

        ReleaseNotesRenderer.Render(NotesPanel, check.ReleaseNotes,
            "Zu dieser Version liegen im Update-Ordner keine Versionshinweise.");

        InstallButton.IsVisible = check.CanInstall;
        SkipButton.Click += (_, _) => CloseWith(UpdateDialogResult.Skip);
        LaterButton.Click += (_, _) => CloseWith(UpdateDialogResult.Later);
        InstallButton.Click += OnInstall;
    }

    private void CloseWith(UpdateDialogResult result)
    {
        Result = result;
        Close();
    }

    private void SetButtonsEnabled(bool enabled) =>
        SkipButton.IsEnabled = LaterButton.IsEnabled = InstallButton.IsEnabled = enabled;

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (_updates is null || _check is null) return;

        SetButtonsEnabled(false);
        ProgressPanel.IsVisible = true;
        var progress = new Progress<string>(text => ProgressText.Text = text);

        try
        {
            bool started = await _updates.DownloadAndApplyAsync(_check, progress);
            if (!started)
            {
                ProgressText.Text = "Das Update konnte nicht vorbereitet werden. Details stehen im Log.";
                SetButtonsEnabled(true);
                return;
            }

            Result = UpdateDialogResult.Installing;
            ProgressText.Text = "DNS-Pilot wird beendet und neu gestartet …";

            // PFLICHT: Das Austausch-Skript wartet per Wait-Process auf diesen
            // Prozess. Ohne das Beenden wartet es endlos.
            UpdateService.TerminateForUpdate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update-Installation aus dem Update-Dialog fehlgeschlagen.");
            ProgressText.Text = "Das Update ist fehlgeschlagen. Details stehen im Log.";
            SetButtonsEnabled(true);
        }
    }
}
