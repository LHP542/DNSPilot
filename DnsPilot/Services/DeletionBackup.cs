using System.Text;
using System.Text.Json;
using DnsPilot.Models;
using NLog;

namespace DnsPilot.Services;

/// <summary>
/// Hält jeden gelöschten Eintrag fest, <b>bevor</b> er gelöscht wird — eine
/// JSON-Zeile pro Eintrag in
/// <c>%LOCALAPPDATA%\DnsPilot\geloescht\JJJJ-MM-TT.jsonl</c>.
///
/// Der DNS-Server hat keinen Papierkorb. Wer beim Aufräumen den falschen
/// Drucker erwischt, braucht Name, Zone, Adresse und TTL, um ihn wieder
/// anzulegen — und genau das steht hier.
/// </summary>
public sealed class DeletionBackup
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly string _folder;

    public DeletionBackup() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DnsPilot", "geloescht"))
    {
    }

    /// <summary>Konstruktor mit explizitem Ordner — für Tests.</summary>
    public DeletionBackup(string folder) => _folder = folder;

    public string Folder => _folder;

    /// <summary>
    /// Schreibt den Eintrag in die Tagesdatei. Wirft bei Fehlern: ohne Sicherung
    /// wird nicht gelöscht.
    /// </summary>
    public string Write(DnsRecord record, string server, DateTime nowUtc)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, $"{nowUtc.ToLocalTime():yyyy-MM-dd}.jsonl");

        var entry = new BackupEntry(
            nowUtc,
            server,
            Environment.UserName,
            record.Zone,
            record.OwnerName,
            record.Type.ToString(),
            record.Data,
            record.Ttl,
            record.TimestampUtc);

        // Ein Eintrag pro Zeile, angehängt: eine abgebrochene Schreibaktion
        // beschädigt höchstens die letzte Zeile, nie die bisherigen.
        File.AppendAllText(path, JsonSerializer.Serialize(entry) + "\n", new UTF8Encoding(false));
        Log.Info("Sicherung vor dem Löschen: {0} {1} {2} → {3}", record.OwnerName, record.Type, record.Data, path);
        return path;
    }

    /// <summary>Liest eine Sicherungsdatei zurück (für Tests und eine spätere Wiederherstellung).</summary>
    public static IReadOnlyList<BackupEntry> Read(string path) =>
        File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<BackupEntry>(l))
            .OfType<BackupEntry>()
            .ToList();

    public sealed record BackupEntry(
        DateTime DeletedUtc,
        string Server,
        string DeletedBy,
        string Zone,
        string OwnerName,
        string Type,
        string Data,
        uint Ttl,
        DateTime? TimestampUtc);
}
