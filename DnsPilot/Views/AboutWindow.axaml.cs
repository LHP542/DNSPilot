using System.Runtime.Versioning;
using Avalonia.Interactivity;
using DnsPilot.Services;
using NLog;

namespace DnsPilot.Views;

/// <summary>
/// Über-Fenster mit Version, manueller Update-Prüfung und dem echten
/// Self-Update. Eine reine „Version X ist verfügbar"-Meldung wäre zu wenig —
/// niemand soll ein ZIP von Hand auf den Programmordner kopieren.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class AboutWindow : ChromeWindow
{
    private const string GithubUrl = "https://github.com/LHP542/DNSPilot";

    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly UpdateService? _updates;
    private readonly IShellServices? _shell;
    private UpdateCheckResult? _lastCheck;

    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public AboutWindow()
    {
        InitializeComponent();
    }

    internal AboutWindow(UpdateService updates, IShellServices shell) : this()
    {
        _updates = updates;
        _shell = shell;

        VersionText.Text = $"Version {updates.CurrentVersionDisplay}";
        ChannelText.Text = "Updates kommen aus dem Netzwerkordner, der in den Einstellungen hinterlegt ist.";

        CheckButton.Click += OnCheckUpdate;
        InstallButton.Click += OnInstallUpdate;
        GithubButton.Click += (_, _) => _ = shell.OpenExternalAsync(GithubUrl);
        LogButton.Click += (_, _) => _ = shell.OpenExternalAsync(LogFolder());
    }

    private static string LogFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DnsPilot", "logs");

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updates is null) return;

        CheckButton.IsEnabled = false;
        InstallButton.IsVisible = false;
        UpdateResult.Text = "Wird geprüft …";

        try
        {
            _lastCheck = await _updates.CheckForUpdateAsync(force: true);

            if (_lastCheck.UpdateAvailable)
            {
                UpdateResult.Text = $"Version {_lastCheck.LatestVersion} ist verfügbar "
                                    + $"(installiert: {_lastCheck.CurrentVersion}).";
                InstallButton.IsVisible = _lastCheck.CanInstall;
            }
            else if (_lastCheck.Problem is { } problem)
            {
                UpdateResult.Text = problem;
            }
            else
            {
                UpdateResult.Text = "Die installierte Version ist aktuell.";
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Update-Prüfung im Über-Fenster fehlgeschlagen.");
            UpdateResult.Text = "Die Prüfung ist fehlgeschlagen. Details stehen im Log.";
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updates is null || _lastCheck is null) return;

        InstallButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;

        var progress = new Progress<string>(text => UpdateResult.Text = text);

        try
        {
            bool started = await _updates.DownloadAndApplyAsync(_lastCheck, progress);
            if (!started)
            {
                UpdateResult.Text = "Das Update konnte nicht vorbereitet werden. Details stehen im Log.";
                return;
            }

            UpdateResult.Text = "DNS-Pilot wird beendet und neu gestartet …";

            // PFLICHT: Das Austausch-Skript wartet per Wait-Process auf diesen
            // Prozess. Ohne das Beenden wartet es endlos, und die Anzeige bliebe
            // genau hier stehen.
            UpdateService.TerminateForUpdate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update-Installation fehlgeschlagen.");
            UpdateResult.Text = "Das Update ist fehlgeschlagen. Details stehen im Log.";
        }
        finally
        {
            UpdateProgress.IsVisible = false;
            InstallButton.IsEnabled = true;
            CheckButton.IsEnabled = true;
        }
    }
}
