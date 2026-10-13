namespace DnsPilot.Models;

/// <summary>
/// Persistierte Einstellungen. Alle Felder sind nullable bzw. haben
/// Standardwerte, damit eine ältere Datei ohne das jeweilige Feld weiter
/// gelesen werden kann — ein fehlendes Feld darf nie den Start verhindern.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// DNS-Server (Name oder IP). Leer = der Anmeldeserver dieser Sitzung
    /// (<c>%LOGONSERVER%</c>). In der LHP-Domäne laufen DNS auf DC01–DC03.
    /// </summary>
    public string? Server { get; set; } = "DC03";

    /// <summary>
    /// Optionales abweichendes Konto (typisch das Admin-Konto), wenn DNS-Pilot
    /// nicht ohnehin unter diesem Konto läuft. Leer = angemeldeter Benutzer.
    /// </summary>
    public string? AdminUser { get; set; }

    /// <summary>
    /// Kennwort zum <see cref="AdminUser"/>, DPAPI-geschützt mit <c>ENC1:</c>.
    /// Nur gesetzt, wenn „Kennwort merken" an ist — sonst lebt es nur im
    /// Speicher der laufenden Sitzung.
    /// </summary>
    public string? AdminPasswordProtected { get; set; }

    // ------------------------------------------------------------------
    // Prüfung
    // ------------------------------------------------------------------

    /// <summary>Wartezeit pro Ping in Millisekunden.</summary>
    public int PingTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Gleichzeitige Prüfungen. Bewusst niedrig: ein Scanner, der hundert
    /// Adressen auf einmal anpingt, sieht für Endpoint-Schutz und IDS wie ein
    /// Angreifer aus, der ein Netz abtastet.
    /// </summary>
    public int MaxParallel { get; set; } = 8;

    /// <summary>Pause zwischen zwei gestarteten Prüfungen in Millisekunden.</summary>
    public int PauseBetweenMs { get; set; } = 50;

    /// <summary>
    /// Zusätzlich TCP-Ports anfragen, wenn ICMP nicht antwortet. Aus per
    /// Standard — Portanfragen sind auffälliger als ein Ping.
    /// </summary>
    public bool CheckTcpPorts { get; set; }

    /// <summary>Die Ports für die TCP-Prüfung.</summary>
    public List<int> TcpPorts { get; set; } = [445, 3389, 22, 443, 80];

    /// <summary>Namen über den DNS-Resolver auflösen und mit dem Eintrag vergleichen.</summary>
    public bool CheckResolution { get; set; } = true;

    // ------------------------------------------------------------------
    // Bewertung
    // ------------------------------------------------------------------

    /// <summary>Ab wie vielen Tagen ohne Registrierung ein dynamischer Eintrag als veraltet gilt.</summary>
    public int StaleAfterDays { get; set; } = 30;

    /// <summary>
    /// Ab wie vielen Tagen ohne Antwort eine Adresse zum Aufräum-Kandidaten
    /// wird (zusammen mit <see cref="CandidateMinChecks"/>).
    /// </summary>
    public int CandidateAfterDays { get; set; } = 30;

    /// <summary>Mindestzahl fehlgeschlagener Prüfungen für einen Kandidaten.</summary>
    public int CandidateMinChecks { get; set; } = 3;

    /// <summary>TTL für neu angelegte Einträge in Sekunden.</summary>
    public uint DefaultTtl { get; set; } = 3600;

    // ------------------------------------------------------------------
    // Updates und Fenster
    // ------------------------------------------------------------------

    /// <summary>
    /// Update-Kanal. Ein UNC-Pfad oder lokaler Ordner wird als Ordner-Kanal
    /// erkannt — der Kanaltyp ergibt sich aus der Schreibweise.
    /// </summary>
    public string? UpdateChannel { get; set; } = @"\\samba01\542$\5424_IT-Basis-Dienste\DnsPilot";

    /// <summary>
    /// Automatisch nach Updates sehen: beim Start und danach alle
    /// <see cref="UpdateCheckMinutes"/> Minuten. Der Name stammt aus der Zeit,
    /// als nur beim Start geprüft wurde — bleibt für ältere Dateien so.
    /// </summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>Abstand der automatischen Update-Prüfung im laufenden Betrieb.</summary>
    public const int UpdateCheckMinutes = 30;

    /// <summary>Version, die der Nutzer mit „Diese Version überspringen" abgelehnt hat.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>
    /// Zuletzt gestartete Version. Ist die laufende neuer, zeigt DNS-Pilot
    /// einmal „Was ist neu". Null beim allerersten Start — dann nicht.
    /// </summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>Zuletzt gesuchte Begriffe, neueste zuerst.</summary>
    public List<string> RecentSearches { get; set; } = [];

    public const int MaxRecentSearches = 15;

    // ------------------------------------------------------------------
    // Zuletzt benutzte Ansicht
    // ------------------------------------------------------------------

    /// <summary>Filter „Zone"; null = alle Zonen.</summary>
    public string? FilterZone { get; set; }

    /// <summary>Filter „Typ"; null = alle Typen.</summary>
    public string? FilterType { get; set; }

    public bool FilterOnlyProblems { get; set; }

    public bool FilterOnlyStatic { get; set; }

    /// <summary>Fenstergröße im Normalzustand (nicht maximiert).</summary>
    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    /// <summary>Fensterposition in Bildschirmpixeln.</summary>
    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>Breite der Detailspalte rechts.</summary>
    public double? DetailPaneWidth { get; set; }

    /// <summary>
    /// Vom Nutzer verbreiterte Tabellenspalten, nach Spaltenüberschrift.
    /// Nur Spalten, die er selbst gezogen hat — die übrigen behalten ihre
    /// proportionale Breite und wachsen mit dem Fenster.
    /// </summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = [];

    /// <summary>
    /// Nimmt einen Suchbegriff in die Verlaufsliste auf: nach vorn, ohne
    /// Duplikate (Groß-/Kleinschreibung egal), auf <see cref="MaxRecentSearches"/>
    /// gekürzt.
    /// </summary>
    public void RememberSearch(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return;

        string trimmed = term.Trim();
        RecentSearches.RemoveAll(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase));
        RecentSearches.Insert(0, trimmed);
        if (RecentSearches.Count > MaxRecentSearches)
        {
            RecentSearches.RemoveRange(MaxRecentSearches, RecentSearches.Count - MaxRecentSearches);
        }
    }

    /// <summary>Tiefe Kopie für das Einstellungen-Fenster (Abbrechen muss verwerfen können).</summary>
    public AppSettings Clone() => new()
    {
        Server = Server,
        AdminUser = AdminUser,
        AdminPasswordProtected = AdminPasswordProtected,
        PingTimeoutMs = PingTimeoutMs,
        MaxParallel = MaxParallel,
        PauseBetweenMs = PauseBetweenMs,
        CheckTcpPorts = CheckTcpPorts,
        TcpPorts = [.. TcpPorts],
        CheckResolution = CheckResolution,
        StaleAfterDays = StaleAfterDays,
        CandidateAfterDays = CandidateAfterDays,
        CandidateMinChecks = CandidateMinChecks,
        DefaultTtl = DefaultTtl,
        UpdateChannel = UpdateChannel,
        CheckUpdatesOnStart = CheckUpdatesOnStart,
        SkippedUpdateVersion = SkippedUpdateVersion,
        LastSeenVersion = LastSeenVersion,
        RecentSearches = [.. RecentSearches],
        FilterZone = FilterZone,
        FilterType = FilterType,
        FilterOnlyProblems = FilterOnlyProblems,
        FilterOnlyStatic = FilterOnlyStatic,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowX = WindowX,
        WindowY = WindowY,
        WindowMaximized = WindowMaximized,
        DetailPaneWidth = DetailPaneWidth,
        ColumnWidths = new Dictionary<string, double>(ColumnWidths),
    };
}
