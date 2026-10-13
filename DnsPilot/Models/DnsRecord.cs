using System.Net;
using DnsPilot.Services;

namespace DnsPilot.Models;

/// <summary>Die Eintragstypen, die DNS-Pilot lädt und auswertet.</summary>
public enum RecordType
{
    A,
    AAAA,
    CNAME,
    PTR,
}

/// <summary>
/// Eine DNS-Zone, wie der Server sie meldet.
/// </summary>
/// <param name="Name">Zonenname ohne abschließenden Punkt, z. B. <c>lhp.intern</c>.</param>
/// <param name="IsReverse">Reverse-Lookupzone (<c>…in-addr.arpa</c>/<c>…ip6.arpa</c>).</param>
/// <param name="ZoneType">Primär, sekundär, Stub oder Weiterleitung.</param>
/// <param name="IsDsIntegrated">Im Active Directory gespeichert.</param>
public sealed record DnsZone(string Name, bool IsReverse, ZoneKind ZoneType, bool IsDsIntegrated)
{
    /// <summary>Nur primäre Zonen lassen sich auf diesem Server ändern.</summary>
    public bool IsWritable => ZoneType == ZoneKind.Primary;

    public override string ToString() => Name;
}

/// <summary>Zonentyp laut <c>MicrosoftDNS_Zone.ZoneType</c>.</summary>
public enum ZoneKind
{
    Cache = 0,
    Primary = 1,
    Secondary = 2,
    Stub = 3,
    Forwarder = 4,
}

/// <summary>
/// Ein einzelner Ressourceneintrag.
/// </summary>
/// <param name="Zone">Zone, in der der Eintrag liegt (<c>ContainerName</c>).</param>
/// <param name="OwnerName">Vollständiger Name ohne abschließenden Punkt.</param>
/// <param name="Type">Eintragstyp.</param>
/// <param name="Data">
/// Bei A/AAAA die Adresse, bei CNAME/PTR der Zielname ohne abschließenden Punkt.
/// </param>
/// <param name="Ttl">Gültigkeitsdauer in Sekunden.</param>
/// <param name="TimestampUtc">
/// Zeitpunkt der letzten dynamischen Registrierung. <c>null</c> = statischer
/// Eintrag — von Hand angelegt, er altert nie und wird nie automatisch entfernt.
/// </param>
public sealed record DnsRecord(
    string Zone,
    string OwnerName,
    RecordType Type,
    string Data,
    uint Ttl,
    DateTime? TimestampUtc)
{
    /// <summary>Statische Einträge haben keinen Zeitstempel.</summary>
    public bool IsStatic => TimestampUtc is null;

    /// <summary>
    /// Eindeutiger Schlüssel über Zone, Typ, Name und Daten. Groß-/Kleinschreibung
    /// spielt im DNS keine Rolle und darf es hier auch nicht.
    ///
    /// <b>Die Zone gehört dazu:</b> Derselbe Name kann in einer Zone und in
    /// einer später angelegten Unterzone stehen (z. B. in
    /// <c>20.10.in-addr.arpa</c> und <c>255.20.10.in-addr.arpa</c>). Das sind
    /// zwei Einträge, die getrennt gelöscht werden — ohne die Zone sahen sie
    /// gleich aus, und das Laden auf dem Jumphost brach ab.
    /// </summary>
    public string Key => $"{Zone.ToLowerInvariant()}|{Type}|{OwnerName.ToLowerInvariant()}|{Data.ToLowerInvariant()}";

    /// <summary>
    /// Die IP-Adresse, um die es geht: bei A/AAAA der Inhalt, bei PTR die aus
    /// dem Namen zurückgerechnete Adresse. Sonst null.
    /// </summary>
    public IPAddress? Address => Type switch
    {
        RecordType.A or RecordType.AAAA => IPAddress.TryParse(Data, out var ip) ? ip : null,
        RecordType.PTR => ReverseZone.TryParsePtrOwner(OwnerName, out var ip) ? ip : null,
        _ => null,
    };

    /// <summary>Zielname bei CNAME und PTR, sonst null.</summary>
    public string? Target => Type is RecordType.CNAME or RecordType.PTR ? Data : null;

    /// <summary>
    /// Name relativ zur Zone, so wie ihn die DNS-Konsole zeigt. Ein Eintrag an
    /// der Zonenspitze erscheint dort als „(identisch mit übergeordnetem Ordner)".
    /// </summary>
    public string HostLabel
    {
        get
        {
            if (OwnerName.Equals(Zone, StringComparison.OrdinalIgnoreCase)) return "(Zonenspitze)";

            string suffix = "." + Zone;
            return OwnerName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? OwnerName[..^suffix.Length]
                : OwnerName;
        }
    }

    public override string ToString() => $"{OwnerName} {Type} {Data}";
}
