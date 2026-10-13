using System.Globalization;
using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>
/// Übersetzt die Zeilen, die das Lese-Skript von <see cref="PowerShellDnsServer"/>
/// ausgibt, in <see cref="DnsZone"/> und <see cref="DnsRecord"/>.
///
/// Das Skript schreibt bewusst keine Objekte (JSON, CliXml), sondern eine
/// tab-getrennte Zeile pro Zone bzw. Eintrag: Windows PowerShell 5.1 braucht
/// für <c>ConvertTo-Json</c> über zehntausende Einträge spürbar lange, und die
/// Zeilen lassen sich während des Laufs schon lesen (Fortschritt pro Zone).
/// <code>
/// N  &lt;Anzahl Zonen&gt;
/// Z  &lt;Zone&gt;  &lt;ZoneType&gt;  &lt;IsReverse&gt;  &lt;IsDsIntegrated&gt;  &lt;IsAutoCreated&gt;
/// R  &lt;Zone&gt;  &lt;HostName&gt;  &lt;Typ&gt;  &lt;TTL s&gt;  &lt;Zeitstempel ISO-UTC oder leer&gt;  &lt;Daten&gt;
/// E  &lt;Zone&gt;  &lt;Fehlermeldung&gt;
/// P  &lt;Zone&gt;                       (Zone fertig)
/// </code>
/// </summary>
public static class DnsRecordMapper
{
    public const char Tab = '\t';

    /// <summary>
    /// Zonen, die DNS-Pilot nicht zeigt: die Vertrauensanker und die
    /// automatisch angelegten Reverse-Zonen für 0/127/255 — die gehören dem
    /// Server und werden nie gepflegt.
    /// </summary>
    public static bool IsInternalZone(string? name, bool autoCreated = false) =>
        autoCreated
        || string.IsNullOrWhiteSpace(name)
        || name.StartsWith("..", StringComparison.Ordinal)
        || name == "."
        || name.Equals("TrustAnchors", StringComparison.OrdinalIgnoreCase)
        || name is "0.in-addr.arpa" or "127.in-addr.arpa" or "255.in-addr.arpa";

    /// <summary>Zeile <c>Z</c> → Zone, oder null für ausgeblendete Zonen.</summary>
    public static DnsZone? ParseZone(string line)
    {
        string[] f = line.Split(Tab);
        if (f.Length < 6 || f[0] != "Z") return null;

        string name = Normalize(f[1]);
        if (IsInternalZone(name, IsTrue(f[5]))) return null;
        if (!Enum.TryParse<ZoneKind>(f[2], ignoreCase: true, out var kind) || kind == ZoneKind.Cache) return null;

        return new DnsZone(name, IsTrue(f[3]), kind, IsTrue(f[4]));
    }

    /// <summary>Zeile <c>R</c> → Eintrag, oder null bei einem Typ, den DNS-Pilot nicht auswertet.</summary>
    public static DnsRecord? ParseRecord(string line)
    {
        string[] f = line.Split(Tab);
        if (f.Length < 7 || f[0] != "R") return null;

        if (!Enum.TryParse<RecordType>(f[3], ignoreCase: false, out var type) || !Enum.IsDefined(type)) return null;

        string zone = Normalize(f[1]);
        string data = Normalize(f[6]);
        if (zone.Length == 0 || f[2].Length == 0 || data.Length == 0) return null;

        uint ttl = uint.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out uint t) ? t : 0;
        DateTime? stamp = DateTime.TryParse(f[5], CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

        return new DnsRecord(zone, OwnerFqdn(f[2], zone), type, data, ttl, stamp);
    }

    /// <summary><c>@</c> → Zone, sonst <c>name.zone</c>. So liefert <c>Get-DnsServerResourceRecord</c> den HostName.</summary>
    public static string OwnerFqdn(string hostName, string zone)
    {
        string host = Normalize(hostName);
        if (host is "@" or "") return zone;
        // Manche Einträge kommen bereits vollständig qualifiziert zurück.
        return host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase) || host.Equals(zone, StringComparison.OrdinalIgnoreCase)
            ? host
            : $"{host}.{zone}";
    }

    /// <summary>Gegenrichtung für Anlegen und Löschen: vollständiger Name → Name relativ zur Zone.</summary>
    public static string NodeName(string ownerFqdn, string zone)
    {
        string owner = Normalize(ownerFqdn);
        if (owner.Equals(zone, StringComparison.OrdinalIgnoreCase)) return "@";

        string suffix = "." + zone;
        return owner.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? owner[..^suffix.Length]
            : throw new ArgumentException($"{ownerFqdn} liegt nicht in der Zone {zone}.", nameof(ownerFqdn));
    }

    /// <summary>Abschließenden Punkt weg, Leerraum weg. Die Schreibweise bleibt.</summary>
    public static string Normalize(string value) => value.Trim().TrimEnd('.');

    private static bool IsTrue(string value) => bool.TryParse(value, out bool b) && b;
}
