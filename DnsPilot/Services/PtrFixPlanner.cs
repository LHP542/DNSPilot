using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>Ein PTR, der für einen A-Eintrag angelegt werden kann.</summary>
/// <param name="Host">Der A-Eintrag, dessen Name der PTR nennen soll.</param>
/// <param name="ReverseZone">Die Reverse-Zone, in die der PTR kommt.</param>
public sealed record PtrFix(DnsRecord Host, DnsZone ReverseZone)
{
    public override string ToString() => $"{Host.Data} → {Host.OwnerName} ({ReverseZone.Name})";
}

/// <summary>
/// Entscheidet, für welche A-Einträge „PTR anlegen" eindeutig ist. Angeboten
/// wird es nur, wenn die Zuordnung Adresse → Name keine Frage offen lässt:
/// <list type="bullet">
///   <item>es gibt für die Adresse noch gar keinen PTR,</item>
///   <item>die Reverse-Zone existiert und ist beschreibbar,</item>
///   <item>kein anderer Host-Name zeigt auf dieselbe Adresse — sonst wäre
///   unklar, welcher Name in den PTR gehört (Befund „doppelte Adresse"),</item>
///   <item>der Eintrag gehört nicht dem Domänencontroller (Zonenspitze,
///   DomainDnsZones usw.) — dessen PTR nennt den DC-Namen.</item>
/// </list>
/// </summary>
public static class PtrFixPlanner
{
    /// <summary>Die anlegbaren PTR für eine Auswahl; höchstens einer je Adresse.</summary>
    public static IReadOnlyList<PtrFix> Plan(DnsInventory inventory, IEnumerable<DnsRecord> records)
    {
        var result = new List<PtrFix>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (For(inventory, record) is { } fix && seen.Add(fix.Host.Data)) result.Add(fix);
        }
        return result;
    }

    /// <summary>Der anlegbare PTR für einen Eintrag, oder null.</summary>
    public static PtrFix? For(DnsInventory inventory, DnsRecord record)
    {
        if (record.Type != RecordType.A || !Ipv4.TryToUInt32(record.Address, out uint ip)) return null;
        if (DnsAnalyzer.IsZoneApexOrAdService(record, inventory)) return null;
        if (inventory.ReverseZoneFor(ip) is not { IsWritable: true } zone) return null;
        if (inventory.PtrRecordsAt(ip).Count > 0) return null;

        bool otherNames = inventory.ARecordsAt(ip).Any(a =>
            !a.OwnerName.Equals(record.OwnerName, StringComparison.OrdinalIgnoreCase)
            && !DnsAnalyzer.IsZoneApexOrAdService(a, inventory));
        return otherNames ? null : new PtrFix(record, zone);
    }
}
