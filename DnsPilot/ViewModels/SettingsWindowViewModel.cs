using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsPilot.Models;
using DnsPilot.Services;
using NLog;

namespace DnsPilot.ViewModels;

/// <summary>
/// Einstellungen. Arbeitet auf einer Kopie, damit „Abbrechen" wirklich verwirft
/// — sonst wäre ein halb eingetippter Servername sofort scharf.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SettingsWindowViewModel : ViewModelBase
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly SettingsService _settings;
    private readonly IDnsServer _server;
    private readonly CredentialStore _credentials;

    public SettingsWindowViewModel(SettingsService settings, IDnsServer server, CredentialStore credentials)
    {
        _settings = settings;
        _server = server;
        _credentials = credentials;

        var c = settings.Current;
        _serverName = c.Server ?? string.Empty;
        _adminUser = c.AdminUser ?? string.Empty;
        // Das gespeicherte Kennwort wird NICHT ins Feld gelegt. Ein Hinweis
        // zeigt, dass eines bekannt ist; ein leeres Feld lässt es unverändert.
        _hasStoredPassword = credentials.HasPassword;
        _rememberPassword = SecretProtection.IsProtected(c.AdminPasswordProtected);
        _pingTimeoutMs = c.PingTimeoutMs;
        _maxParallel = c.MaxParallel;
        _pauseBetweenMs = c.PauseBetweenMs;
        _checkTcpPorts = c.CheckTcpPorts;
        _tcpPortsText = string.Join(", ", c.TcpPorts);
        _checkResolution = c.CheckResolution;
        _staleAfterDays = c.StaleAfterDays;
        _candidateAfterDays = c.CandidateAfterDays;
        _candidateMinChecks = c.CandidateMinChecks;
        _defaultTtl = (int)c.DefaultTtl;
        _updateChannel = c.UpdateChannel ?? string.Empty;
        _checkUpdatesOnStart = c.CheckUpdatesOnStart;
    }

    [ObservableProperty] private string _serverName;
    [ObservableProperty] private string _adminUser;
    [ObservableProperty] private string _adminPassword = string.Empty;
    [ObservableProperty] private bool _hasStoredPassword;
    [ObservableProperty] private bool _rememberPassword;
    [ObservableProperty] private int _pingTimeoutMs;
    [ObservableProperty] private int _maxParallel;
    [ObservableProperty] private int _pauseBetweenMs;
    [ObservableProperty] private bool _checkTcpPorts;
    [ObservableProperty] private string _tcpPortsText;
    [ObservableProperty] private bool _checkResolution;
    [ObservableProperty] private int _staleAfterDays;
    [ObservableProperty] private int _candidateAfterDays;
    [ObservableProperty] private int _candidateMinChecks;
    [ObservableProperty] private int _defaultTtl;
    [ObservableProperty] private string _updateChannel;
    [ObservableProperty] private bool _checkUpdatesOnStart;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;

    /// <summary>Wird true gesetzt, wenn der Nutzer gespeichert hat.</summary>
    public bool Saved { get; private set; }

    /// <summary>Das Fenster hängt sich hier ein, um sich selbst zu schließen.</summary>
    public event EventHandler? CloseRequested;

    public string AutoServerHint
    {
        get
        {
            string? logon = Environment.GetEnvironmentVariable("LOGONSERVER")?.TrimStart('\\');
            return string.IsNullOrWhiteSpace(logon)
                ? "Leer lassen = Anmeldeserver (keiner erkannt)"
                : $"Leer lassen = Anmeldeserver ({logon})";
        }
    }

    private bool CanRun() => !IsBusy;

    /// <summary>
    /// Probiert die eingetragenen Werte aus. Dafür werden sie kurz übernommen
    /// und bei einem Fehlschlag wieder zurückgerollt — der DNS-Zugriff liest
    /// seine Konfiguration aus den Einstellungen, und ein zweiter Weg dorthin
    /// wäre eine Fehlerquelle mehr.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        TestResult = "Verbindung wird geprüft …";
        TestSucceeded = false;

        var backup = _settings.Current;
        try
        {
            ApplyPassword();
            _settings.Replace(BuildSettings());
            _server.Invalidate();

            var info = await _server.ConnectAsync(CancellationToken.None).ConfigureAwait(true);
            TestResult = $"Verbindung steht: {info.StatusText}";
            TestSucceeded = true;
        }
        catch (DnsAccessException ex)
        {
            TestResult = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Verbindungstest fehlgeschlagen.");
            TestResult = "Verbindungstest fehlgeschlagen. Details stehen im Log.";
        }
        finally
        {
            if (!TestSucceeded)
            {
                _settings.Replace(backup);
                _server.Invalidate();
            }
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Save()
    {
        ApplyPassword();
        _settings.Replace(BuildSettings());
        _server.Invalidate();
        Saved = true;

        Log.Info("Einstellungen gespeichert (Server='{0}', Konto='{1}', parallel={2}, TCP={3}).",
            ServerName, AdminUser, MaxParallel, CheckTcpPorts);

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Saved = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Vergisst das Kennwort — im Speicher und in der Datei.</summary>
    [RelayCommand]
    private void ClearStoredPassword()
    {
        AdminPassword = string.Empty;
        HasStoredPassword = false;
        RememberPassword = false;
        _credentials.SetSessionPassword(null);
    }

    /// <summary>Ein eingetipptes Kennwort gilt sofort für diese Sitzung.</summary>
    private void ApplyPassword()
    {
        if (AdminPassword.Length > 0) _credentials.SetSessionPassword(AdminPassword);
    }

    internal AppSettings BuildSettings()
    {
        var s = _settings.Current.Clone();

        s.Server = Trimmed(ServerName);
        s.AdminUser = Trimmed(AdminUser);
        s.PingTimeoutMs = Math.Clamp(PingTimeoutMs, 100, 10_000);
        s.MaxParallel = Math.Clamp(MaxParallel, 1, 32);
        s.PauseBetweenMs = Math.Clamp(PauseBetweenMs, 0, 5_000);
        s.CheckTcpPorts = CheckTcpPorts;
        s.TcpPorts = ParsePorts(TcpPortsText);
        s.CheckResolution = CheckResolution;
        s.StaleAfterDays = Math.Clamp(StaleAfterDays, 1, 3650);
        s.CandidateAfterDays = Math.Clamp(CandidateAfterDays, 1, 3650);
        s.CandidateMinChecks = Math.Clamp(CandidateMinChecks, 1, 1000);
        s.DefaultTtl = (uint)Math.Clamp(DefaultTtl, 60, 604_800);
        s.UpdateChannel = Trimmed(UpdateChannel);
        s.CheckUpdatesOnStart = CheckUpdatesOnStart;

        if (s.AdminUser is null)
        {
            // Kein Konto mehr eingetragen: das Kennwort hat keinen Zweck mehr.
            s.AdminPasswordProtected = null;
            _credentials.SetSessionPassword(null);
        }
        else if (!RememberPassword)
        {
            // Nur für die Sitzung — aus der Datei entfernen.
            s.AdminPasswordProtected = null;
        }
        else if (AdminPassword.Length > 0)
        {
            s.AdminPasswordProtected = SecretProtection.Protect(AdminPassword);
        }
        else if (!HasStoredPassword)
        {
            s.AdminPasswordProtected = null;
        }

        return s;
    }

    /// <summary>„445, 3389 22" → [445, 3389, 22]; Unsinn wird übergangen.</summary>
    internal static List<int> ParsePorts(string text)
    {
        var ports = text
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : 0)
            .Where(p => p is > 0 and <= 65535)
            .Distinct()
            .Take(10)
            .ToList();

        return ports.Count > 0 ? ports : [445, 3389];
    }

    private static string? Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
