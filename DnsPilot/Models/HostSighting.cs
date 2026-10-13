namespace DnsPilot.Models;

/// <summary>
/// Was DNS-Pilot über die Erreichbarkeit einer Adresse weiß — über alle
/// bisherigen Prüfungen hinweg.
///
/// <b>Warum eine Historie und nicht nur das letzte Ergebnis:</b> Ein einzelner
/// fehlgeschlagener Ping beweist nichts. Ein ausgeschaltetes Notebook, ein
/// Server hinter einer Firewall und ein längst verschrotteter Rechner sehen in
/// diesem Moment gleich aus. Erst „seit 30 Tagen bei keiner Prüfung erreicht"
/// trägt eine Löschentscheidung.
/// </summary>
public sealed class HostSighting
{
    /// <summary>Erste Prüfung überhaupt.</summary>
    public DateTime FirstCheckedUtc { get; set; }

    /// <summary>Letzte Prüfung.</summary>
    public DateTime LastCheckedUtc { get; set; }

    /// <summary>Letzte Prüfung mit Antwort. Null = nie erreicht.</summary>
    public DateTime? LastOnlineUtc { get; set; }

    /// <summary>Wie die letzte Antwort kam (<c>ICMP</c>, <c>TCP 445</c> …).</summary>
    public string? LastMethod { get; set; }

    /// <summary>Anzahl aller Prüfungen.</summary>
    public int Checks { get; set; }

    /// <summary>Prüfungen ohne Antwort seit der letzten erfolgreichen (bzw. seit Beginn).</summary>
    public int FailuresSinceOnline { get; set; }

    /// <summary>War die letzte Prüfung erfolgreich?</summary>
    public bool LastWasOnline => LastOnlineUtc is { } online && online >= LastCheckedUtc;
}

/// <summary>Zusammengefasstes Urteil über die Erreichbarkeit.</summary>
public enum Reachability
{
    /// <summary>Noch nie geprüft.</summary>
    Unchecked,

    /// <summary>Hat bei der letzten Prüfung geantwortet.</summary>
    Online,

    /// <summary>Hat zuletzt nicht geantwortet — noch kein Grund zur Sorge.</summary>
    Offline,

    /// <summary>
    /// Seit langer Zeit bei mehreren Prüfungen nie erreicht. Ein Kandidat zum
    /// Aufräumen — aber immer noch nur ein Kandidat.
    /// </summary>
    Candidate,
}
