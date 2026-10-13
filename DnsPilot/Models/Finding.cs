namespace DnsPilot.Models;

/// <summary>Gewicht eines Befunds. Die Reihenfolge ist die Sortierreihenfolge.</summary>
public enum Severity
{
    None = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>Was an einem Eintrag auffällt.</summary>
public enum FindingKind
{
    /// <summary>Für das Netz gibt es keine Reverse-Zone — ein PTR ist gar nicht möglich.</summary>
    NoReverseZone,

    /// <summary>Reverse-Zone vorhanden, aber kein PTR für diese Adresse.</summary>
    MissingPtr,

    /// <summary>PTR und A widersprechen sich.</summary>
    PtrMismatch,

    /// <summary>
    /// PTR zeigt auf einen Namen ohne A-Eintrag, und der PTR ist statisch oder
    /// wird nicht mehr erneuert — ein Überrest.
    /// </summary>
    PtrOrphan,

    /// <summary>
    /// PTR zeigt auf einen Namen ohne A-Eintrag, wird aber aktiv registriert.
    /// Das Problem ist der fehlende A-Eintrag, nicht der PTR.
    /// </summary>
    MissingA,

    /// <summary>PTR zeigt auf einen Namen außerhalb der Zonen dieses Servers.</summary>
    PtrExternal,

    /// <summary>CNAME zeigt auf einen Namen, den es in den eigenen Zonen nicht gibt.</summary>
    CnameDangling,

    /// <summary>
    /// Dieselbe Adresse hängt an mehreren Namen, und kein PTR bestätigt einen
    /// davon als Hauptnamen.
    /// </summary>
    DuplicateIp,

    /// <summary>
    /// Zusätzlicher Name für eine Adresse, deren PTR auf einen Hauptnamen mit
    /// derselben Adresse zeigt — DNS-seitig stimmig, ob gewollt, entscheidet
    /// der Mensch.
    /// </summary>
    AliasName,

    /// <summary>Dynamischer Eintrag, der sich lange nicht mehr registriert hat.</summary>
    Stale,

    /// <summary>
    /// Eintrag liegt in einer Zone, für deren Namen es eine speziellere
    /// Unterzone gibt. Clients fragen die Unterzone und sehen ihn nie.
    /// </summary>
    Shadowed,
}

/// <summary>Ein einzelner Befund zu einem Eintrag.</summary>
public sealed record Finding(FindingKind Kind, Severity Severity, string Text)
{
    public override string ToString() => Text;
}
