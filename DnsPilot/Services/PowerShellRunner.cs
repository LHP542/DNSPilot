using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using NLog;

namespace DnsPilot.Services;

/// <summary>Ergebnis eines PowerShell-Laufs.</summary>
public sealed record PowerShellResult(int ExitCode, IReadOnlyList<string> Output, string Error);

/// <summary>Führt ein PowerShell-Skript aus. Als Schnittstelle, damit <see cref="PowerShellDnsServer"/> testbar ist.</summary>
public interface IPowerShellRunner
{
    /// <summary>
    /// Führt <paramref name="script"/> aus. <paramref name="onLine"/> bekommt jede
    /// Ausgabezeile sofort — für Fortschrittsanzeigen bei langen Läufen.
    /// </summary>
    Task<PowerShellResult> RunAsync(string script, Action<string>? onLine, CancellationToken token);
}

/// <summary>
/// Startet <c>powershell.exe</c> (Windows PowerShell 5.1 — ist auf jedem
/// Windows-Server da, und dort liegt das Modul DnsServer aus den RSAT-Tools).
///
/// <b>Bewusst ohne <c>-EncodedCommand</c> und ohne <c>-ExecutionPolicy Bypass</c>:</b>
/// beide Schalter sind klassische Merkmale von Schadsoftware, und
/// Verhaltens-AV (Trend Micro) reagiert darauf. Das Skript geht als normaler
/// <c>-Command</c>-Text hinein. Die Ausführungsrichtlinie gilt für
/// <c>-Command</c> ohnehin nicht. Damit das ohne Quoting-Ärger geht, enthalten
/// die Skripte <b>keine doppelten Anführungszeichen</b> — Werte werden in einfache
/// Anführungszeichen gesetzt (<see cref="Quote"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerShellRunner(SettingsService settings, CredentialStore credentials) : IPowerShellRunner
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    public static string ExecutablePath => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Setzt einen Wert als PowerShell-Zeichenkette in einfache Anführungszeichen.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public async Task<PowerShellResult> RunAsync(string script, Action<string>? onLine, CancellationToken token)
    {
        if (script.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("PowerShell-Skripte dürfen keine doppelten Anführungszeichen enthalten.", nameof(script));
        }

        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + script);

        ApplyCredentials(psi);

        var output = new List<string>();
        var error = new StringBuilder();
        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.Add(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (error) error.AppendLine(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new DnsAccessException($"PowerShell konnte nicht gestartet werden: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Schon beendet.
            }
            throw;
        }

        Log.Debug("PowerShell beendet mit {0} nach {1} ms, {2} Zeilen.", process.ExitCode, sw.ElapsedMilliseconds, output.Count);
        lock (output) lock (error) return new PowerShellResult(process.ExitCode, [.. output], error.ToString().Trim());
    }

    /// <summary>Startet PowerShell unter dem Admin-Konto aus den Einstellungen, falls eins eingetragen ist.</summary>
    private void ApplyCredentials(ProcessStartInfo psi)
    {
        string? user = settings.Current.AdminUser?.Trim();
        if (string.IsNullOrEmpty(user)) return;

        string password = credentials.GetPassword()
            ?? throw new DnsAccessException($"Für das Konto {user} ist kein Kennwort hinterlegt. Bitte in den Einstellungen eingeben.");

        int slash = user.IndexOf('\\');
        psi.Domain = slash > 0 ? user[..slash] : null;
        psi.UserName = slash > 0 ? user[(slash + 1)..] : user;
        psi.LoadUserProfile = false;
        psi.WorkingDirectory = Environment.SystemDirectory;

        var secure = new SecureString();
        foreach (char c in password) secure.AppendChar(c);
        secure.MakeReadOnly();
        psi.Password = secure;
    }
}
