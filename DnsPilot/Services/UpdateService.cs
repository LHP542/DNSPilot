using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using NLog;

namespace DnsPilot.Services;

/// <summary>Wie der Nutzer den Update-Dialog verlassen hat.</summary>
public enum UpdateDialogResult
{
    Later,
    Skip,
    Installing,
}

/// <summary>Ergebnis einer Update-Prüfung.</summary>
/// <param name="ReleaseNotes">Versionshinweise aller übersprungenen Versionen, neueste zuerst; null wenn keine im Ordner liegen.</param>
public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version? LatestVersion,
    string? PackagePath,
    string? Problem,
    string? ReleaseNotes = null)
{
    public bool UpdateAvailable => LatestVersion is not null && LatestVersion > CurrentVersion;
    public bool CanInstall => UpdateAvailable && PackagePath is not null;
}

/// <summary>
/// Update-Prüfung und echtes Self-Update gegen den Ordner-Kanal im Firmennetz.
///
/// Der Ablauf ist bewusst der aus dem Kroste-Standard, nur mit einem Ordner
/// statt GitHub Releases als Quelle: prüfen → Zustimmung einholen → Paket
/// <b>erst kopieren, dann</b> entpacken → Austausch-Skript starten → <b>App
/// beenden</b>.
///
/// Der letzte Schritt wird gern vergessen und ist genau der, der alles aufhält:
/// das Skript wartet per <c>Wait-Process</c> auf das Ende dieses Prozesses,
/// bevor es Dateien ersetzt. Beendet sich die App nicht selbst, wartet das
/// Skript ewig und die Anzeige bleibt bei „Update wird installiert" stehen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateService(SettingsService settings)
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly SettingsService _settings = settings;
    private UpdateCheckResult? _cached;

    /// <summary>Version dieser Installation, aus dem MinVer-Attribut der Assembly.</summary>
    public Version CurrentVersion { get; } =
        UpdateChannel.ParseAssemblyVersion(
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? Assembly.GetEntryAssembly()?.GetName().Version
        ?? new Version(0, 0, 0);

    /// <summary>Vollständige Versionszeichenkette für die Anzeige (inkl. Vorabkennung).</summary>
    public string CurrentVersionDisplay =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? CurrentVersion.ToString();

    /// <summary>
    /// Sieht im Kanal nach einer neueren Version. Das Ergebnis wird zwischen-
    /// gespeichert; <paramref name="force"/> erzwingt eine neue Prüfung.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(bool force = false)
    {
        if (!force && _cached is not null) return _cached;

        string? channel = _settings.Current.UpdateChannel;
        UpdateCheckResult result;

        if (string.IsNullOrWhiteSpace(channel))
        {
            result = new UpdateCheckResult(CurrentVersion, null, null, "Kein Update-Kanal eingestellt.");
        }
        else if (!UpdateChannel.LooksLikeFolder(channel))
        {
            result = new UpdateCheckResult(CurrentVersion, null, null,
                "Der eingestellte Update-Kanal ist kein Ordnerpfad.");
        }
        else
        {
            result = await Task.Run(() => CheckFolder(channel.Trim(), CurrentVersion)).ConfigureAwait(false);
        }

        _cached = result;
        return result;
    }

    /// <summary>Kern der Prüfung ohne Cache und Assembly-Version — damit testbar.</summary>
    internal static UpdateCheckResult CheckFolder(string folder, Version installed)
    {
        try
        {
            var newest = UpdateChannel.FindNewestPackage(folder);
            if (newest is null)
            {
                // Ein Notebook ohne Netzlaufwerk ist der Normalfall, nicht die
                // Störung — sonst steht im Log jedes mobilen Nutzers täglich ein
                // Fehler, den niemand beheben kann.
                Log.Debug("Im Update-Ordner {0} liegt kein Paket (oder er ist nicht erreichbar).", folder);
                return new UpdateCheckResult(installed, null, null,
                    "Der Update-Ordner ist nicht erreichbar.");
            }

            var latest = newest.Value.Version;
            string? notes = latest > installed ? UpdateChannel.CollectReleaseNotes(folder, installed, latest) : null;

            Log.Info("Update-Prüfung: installiert {0}, im Ordner {1}, Versionshinweise: {2}.",
                installed, latest, notes is null ? "keine" : "ja");
            return new UpdateCheckResult(installed, latest, newest.Value.Path, null, notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Update-Ordner {0} nicht lesbar.", folder);
            return new UpdateCheckResult(installed, null, null,
                "Auf den Update-Ordner konnte nicht zugegriffen werden.");
        }
    }

    /// <summary>
    /// Lädt das Paket, entpackt es daneben und startet das Austausch-Skript.
    /// Bei <c>true</c> MUSS der Aufrufer <see cref="TerminateForUpdate"/> rufen.
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(UpdateCheckResult check, IProgress<string>? progress = null)
    {
        if (!check.CanInstall || check.PackagePath is null) return false;

        try
        {
            string work = Path.Combine(Path.GetTempPath(), $"DnsPilot-Update-{check.LatestVersion}");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);

            // Erst kopieren, dann entpacken. Zwei Gründe: das Paket könnte
            // zwischen Prüfung und Entpacken ausgetauscht werden, und ein
            // Netzlaufwerk, das mitten im Entpacken wegbricht, hinterlässt sonst
            // einen halb ersetzten Programmordner.
            progress?.Report("Paket wird kopiert …");
            string localZip = Path.Combine(work, Path.GetFileName(check.PackagePath));
            await Task.Run(() => File.Copy(check.PackagePath, localZip, overwrite: true)).ConfigureAwait(false);

            progress?.Report("Paket wird entpackt …");
            string payload = Path.Combine(work, "payload");
            await Task.Run(() => ZipFile.ExtractToDirectory(localZip, payload, overwriteFiles: true)).ConfigureAwait(false);

            progress?.Report("Austausch wird vorbereitet …");
            string script = WriteInstallerScript(work, payload);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{script}\"",
                WorkingDirectory = work,
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            Log.Info("Austausch-Skript {0} gestartet — die Anwendung beendet sich jetzt.", script);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update auf {0} fehlgeschlagen.", check.LatestVersion);
            progress?.Report("Das Update ist fehlgeschlagen. Details stehen im Log.");
            return false;
        }
    }

    /// <summary>
    /// Schreibt die Batch-Datei, die auf das Prozessende wartet, die Dateien
    /// ersetzt und neu startet.
    ///
    /// Zwei Fallen sind hier eingebaut vermieden:
    /// (1) Die Zeilen dürfen <b>keine führende Einrückung</b> haben — ein
    ///     eingerücktes <c>:label</c> ist für cmd kein gültiges Sprungziel,
    ///     <c>goto</c> scheitert still, und xcopy läuft los, während die alte
    ///     App die Dateien noch sperrt. Ergebnis: die ALTE Version startet neu.
    /// (2) Auf das Prozessende wird per <c>Wait-Process</c> gewartet (blockiert
    ///     sauber) statt mit einer <c>tasklist</c>-Schleife.
    /// </summary>
    private static string WriteInstallerScript(string workDir, string payloadDir)
    {
        string target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(target, "DnsPilot.exe");
        string logFile = Path.Combine(workDir, "update.log");
        int pid = Environment.ProcessId;

        string[] lines =
        [
            "@echo off",
            $"echo [%date% %time%] Warte auf Ende von PID {pid} >> \"{logFile}\"",
            $"powershell -NoProfile -Command \"Wait-Process -Id {pid} -ErrorAction SilentlyContinue\"",
            "rem Kurzer Nachlauf, damit Windows die Dateihandles freigibt.",
            "ping -n 3 127.0.0.1 > nul",
            $"echo [%date% %time%] Kopiere nach \"{target}\" >> \"{logFile}\"",
            $"xcopy \"{payloadDir}\\*\" \"{target}\\\" /E /Y /I >> \"{logFile}\" 2>&1",
            "if errorlevel 1 goto failed",
            $"echo [%date% %time%] Starte neu >> \"{logFile}\"",
            $"start \"\" \"{exe}\"",
            "goto ende",
            ":failed",
            $"echo [%date% %time%] FEHLER beim Kopieren >> \"{logFile}\"",
            $"start \"\" \"{exe}\"",
            ":ende",
        ];

        string script = Path.Combine(workDir, "install.bat");
        File.WriteAllLines(script, lines);
        return script;
    }

    /// <summary>
    /// Beendet die Anwendung, damit das Austausch-Skript weiterlaufen kann.
    /// Jeder Aufrufer von <see cref="DownloadAndApplyAsync"/> MUSS das bei
    /// Erfolg tun — sonst wartet das Skript endlos.
    /// </summary>
    public static void TerminateForUpdate()
    {
        Log.Info("Beende die Anwendung für den Update-Austausch.");
        LogManager.Shutdown();

        // Fallback: hängt ein Finalizer, schickt Kill direkt TerminateProcess.
        // Der Installer wartet nur auf das Verschwinden der PID — der Austausch
        // muss nicht sauber enden.
        var killer = new Timer(_ => Process.GetCurrentProcess().Kill(), null, 1500, Timeout.Infinite);
        GC.KeepAlive(killer);

        Environment.Exit(0);
    }
}
