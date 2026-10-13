using NLog;

namespace DnsPilot.Services;

/// <summary>
/// Datei-Primitiven für die JSON-Ablage. Zwei Regeln, die sonst gern fehlen:
///
/// 1. <b>Atomar schreiben.</b> Ein <c>WriteAllText</c> direkt auf die Zieldatei
///    hinterlässt bei Absturz oder Stromausfall mitten im Schreiben eine halbe
///    Datei. Stattdessen erst nach <c>&lt;datei&gt;.tmp</c>, dann
///    <c>File.Move(tmp, ziel, overwrite: true)</c> — das Move ist atomar.
///
/// 2. <b>Defekte Daten nicht stillschweigend verlieren.</b> Lässt sich die Datei
///    nicht deserialisieren, wandert sie nach <c>&lt;datei&gt;.broken</c> und
///    bleibt für die Diagnose erhalten. Ohne das überschreibt der nächste Save
///    die kaputte Datei endgültig.
///
/// Bewusst <b>nicht</b> quarantänisiert wird bei IO-Fehlern (Datei gesperrt,
/// Netzlaufwerk kurz weg): dort ist der Inhalt in Ordnung, nur gerade nicht
/// lesbar. Ein Verschieben würde intakte Daten aus dem Weg räumen — also genau
/// den Verlust verursachen, den die Regel verhindern soll.
/// </summary>
internal static class JsonFileStore
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Schreibt <paramref name="json"/> atomar; legt das Verzeichnis an.</summary>
    public static void WriteAtomic(string path, string json)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemp(tmp);
            throw;
        }
    }

    /// <summary>
    /// Verschiebt eine nicht deserialisierbare Datei nach
    /// <c>&lt;datei&gt;.broken</c>. Schlägt das fehl, wird nur geloggt — der
    /// Aufrufer startet in jedem Fall mit Standardwerten weiter.
    /// </summary>
    public static void Quarantine(string path)
    {
        string broken = path + ".broken";
        try
        {
            File.Move(path, broken, overwrite: true);
            Log.Error("Defekte Datei nach {0} gesichert. Es wird mit Standardwerten weitergestartet.", broken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Defekte Datei {0} konnte nicht nach {1} gesichert werden.", path, broken);
        }
    }

    private static void TryDeleteTemp(string tmp)
    {
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Temporäre Datei {0} konnte nicht aufgeräumt werden.", tmp);
        }
    }
}
