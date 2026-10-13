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
    private readonly Action<UpdateCheckResult>? _skip;
    private UpdateCheckResult? _lastCheck;

    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public AboutWindow()
    {
        InitializeComponent();
    }

    /// <param name="skip">Wird gerufen, wenn der Nutzer im Update-Dialog „Diese Version überspringen" wählt.</param>
    internal AboutWindow(UpdateService updates, IShellServices shell, Action<UpdateCheckResult>? skip = null) : this()
    {
        _updates = updates;
        _shell = shell;
        _skip = skip;

        VersionText.Text = $"Version {updates.CurrentVersionDisplay}";
        ChannelText.Text = "Updates kommen aus dem Netzwerkordner, der in den Einstellungen hinterlegt ist.";

        CheckButton.Click += OnCheckUpdate;
        InstallButton.Click += OnInstallUpdate;
        WhatsNewButton.Click += (_, _) => _ = new WhatsNewWindow(
            "Was ist neu",
            $"Alle Versionshinweise bis {updates.CurrentVersionDisplay}, neueste zuerst.",
            Changelog.All(Changelog.LoadEmbedded())).ShowDialog(this);
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
                InstallButton.Content = "Was ist neu? · Installieren …";
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

    /// <summary>
    /// Öffnet den Update-Dialog mit Versionshinweisen. Installiert wird dort —
    /// so gibt es genau einen Weg zum Austausch, und er zeigt immer, was sich ändert.
    /// </summary>
    private async void OnInstallUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updates is null || _lastCheck is null) return;

        try
        {
            var dialog = new UpdateDialog(_updates, _lastCheck);
            await dialog.ShowDialog(this);
            if (dialog.Result == UpdateDialogResult.Skip) _skip?.Invoke(_lastCheck);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update-Dialog aus dem Über-Fenster fehlgeschlagen.");
            UpdateResult.Text = "Der Update-Dialog konnte nicht geöffnet werden. Details stehen im Log.";
        }
    }
}
