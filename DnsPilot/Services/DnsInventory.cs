using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>
/// Alle geladenen Zonen und Einträge mit den Nachschlagetabellen, die Suche und
/// Analyse brauchen: nach Name, nach IPv4-Adresse und nach Reverse-Netz.
///
/// Unveränderlich: Eine Änderung (Anlegen, Löschen) erzeugt per
/// <see cref="With"/> ein neues Inventar. So kann eine laufende Analyse nie
/// eine halb aktualisierte Tabelle sehen.
/// </summary>
public sealed class DnsInventory
{
    private readonly Dictionary<string, List<DnsRecord>> _byOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, List<DnsRecord>> _aByIp = [];
    private readonly Dictionary<uint, List<DnsRecord>> _ptrByIp = [];
    private readonly Dictionary<string, List<DnsRecord>> _cnameByTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DnsZone Zone, IpRange Network)> _reverseNetworks = [];
    private readonly List<DnsZone> _forwardZones;
    private readonly List<DnsZone> _zonesBySpecificity;

    public static DnsInventory Empty { get; } = new([], []);

    public DnsInventory(IReadOnlyList<DnsZone> zones, IReadOnlyList<DnsRecord> records)
    {
        Zones = zones;
        Records = records;

        // Längste Zone zuerst: „sub.lhp.intern" muss vor „lhp.intern" gewinnen.
        _forwardZones = zones
            .Where(z => !z.IsReverse)
            .OrderByDescending(z => z.Name.Length)
            .ToList();

        _zonesBySpecificity = zones.OrderByDescending(z => z.Name.Length).ToList();

        foreach (var zone in zones.Where(z => z.IsReverse))
        {
            if (ReverseZone.TryGetNetwork(zone.Name, out var network)) _reverseNetworks.Add((zone, network));
        }

        // Kleinstes Netz zuerst: 1.16.172.in-addr.arpa schlägt 16.172.in-addr.arpa.
        _reverseNetworks.Sort((a, b) => a.Network.Count.CompareTo(b.Network.Count));

        foreach (var record in records)
        {
            Add(_byOwner, record.OwnerName, record);

            switch (record.Type)
            {
                case RecordType.A when Ipv4.TryToUInt32(record.Address, out uint ip):
                    Add(_aByIp, ip, record);
                    break;
                case RecordType.PTR when Ipv4.TryToUInt32(record.Address, out uint ip):
                    Add(_ptrByIp, ip, record);
                    break;
                case RecordType.CNAME:
                    Add(_cnameByTarget, record.Data, record);
                    break;
            }
        }
    }

    public IReadOnlyList<DnsZone> Zones { get; }

    public IReadOnlyList<DnsRecord> Records { get; }

    /// <summary>Forward-Zonen, alphabetisch — für Auswahllisten.</summary>
    public IEnumerable<DnsZone> ForwardZones => _forwardZones.OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Alle Einträge mit genau diesem Namen.</summary>
    public IReadOnlyList<DnsRecord> RecordsNamed(string ownerName) =>
        _byOwner.TryGetValue(Trim(ownerName), out var list) ? list : [];

    /// <summary>A-Einträge auf diese Adresse.</summary>
    public IReadOnlyList<DnsRecord> ARecordsAt(uint address) =>
        _aByIp.TryGetValue(address, out var list) ? list : [];

    /// <summary>PTR-Einträge für diese Adresse.</summary>
    public IReadOnlyList<DnsRecord> PtrRecordsAt(uint address) =>
        _ptrByIp.TryGetValue(address, out var list) ? list : [];

    /// <summary>CNAMEs, die auf diesen Namen zeigen.</summary>
    public IReadOnlyList<DnsRecord> CnamesPointingTo(string target) =>
        _cnameByTarget.TryGetValue(Trim(target), out var list) ? list : [];

    /// <summary>Die spezifischste Reverse-Zone, die diese Adresse abdeckt — oder null.</summary>
    public DnsZone? ReverseZoneFor(uint address)
    {
        foreach (var (zone, network) in _reverseNetworks)
        {
            if (network.Contains(address)) return zone;
        }
        return null;
    }

    /// <summary>Reverse-Zonen, die sich mit dem Bereich überschneiden.</summary>
    public IReadOnlyList<DnsZone> ReverseZonesOverlapping(IpRange range) =>
        _reverseNetworks
            .Where(r => r.Network.First <= range.Last && r.Network.Last >= range.First)
            .Select(r => r.Zone)
            .ToList();

    /// <summary>
    /// Die speziellste Zone (Forward oder Reverse), zu der ein Name gehört —
    /// die, die ein Client für diesen Namen tatsächlich fragt.
    /// </summary>
    public DnsZone? MostSpecificZoneFor(string fqdn)
    {
        string name = Trim(fqdn);
        foreach (var zone in _zonesBySpecificity)
        {
            if (name.Equals(zone.Name, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + zone.Name, StringComparison.OrdinalIgnoreCase))
            {
                return zone;
            }
        }
        return null;
    }

    /// <summary>Die Forward-Zone, zu der ein Name gehört — oder null, wenn er außerhalb liegt.</summary>
    public DnsZone? ForwardZoneFor(string fqdn)
    {
        string name = Trim(fqdn);
        foreach (var zone in _forwardZones)
        {
            if (name.Equals(zone.Name, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + zone.Name, StringComparison.OrdinalIgnoreCase))
            {
                return zone;
            }
        }
        return null;
    }

    /// <summary>
    /// Alles, was auf Adressen im Bereich verweist: A- und PTR-Einträge und die
    /// CNAMEs, die auf einen dieser A-Namen zeigen.
    /// </summary>
    public IReadOnlyList<DnsRecord> RecordsInRange(IpRange range)
    {
        var result = new List<DnsRecord>();
        var aNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (ip, list) in _aByIp)
        {
            if (!range.Contains(ip)) continue;
            result.AddRange(list);
            foreach (var a in list) aNames.Add(a.OwnerName);
        }

        foreach (var (ip, list) in _ptrByIp)
        {
            if (range.Contains(ip)) result.AddRange(list);
        }

        foreach (string name in aNames)
        {
            result.AddRange(CnamesPointingTo(name));
        }

        return result;
    }

    /// <summary>
    /// Adressen im Bereich, auf die weder ein A noch ein PTR verweist. Über
    /// 65 536 Adressen wird nicht gerechnet — eine solche Liste liest niemand.
    /// </summary>
    public IReadOnlyList<uint> FreeAddresses(IpRange range)
    {
        if (range.Count > 65_536) return [];

        return range.UsableAddresses()
            .Where(ip => !_aByIp.ContainsKey(ip) && !_ptrByIp.ContainsKey(ip))
            .ToList();
    }

    /// <summary>
    /// Einträge, die mit diesem zusammenhängen: bei A die PTRs auf die Adresse,
    /// weitere Namen auf derselben Adresse und CNAMEs auf den Namen; bei PTR die
    /// Einträge des Ziels; bei CNAME die Einträge des Ziels.
    /// </summary>
    public IReadOnlyList<DnsRecord> Related(DnsRecord record)
    {
        var related = new List<DnsRecord>();

        switch (record.Type)
        {
            case RecordType.A or RecordType.AAAA:
                if (Ipv4.TryToUInt32(record.Address, out uint ip))
                {
                    related.AddRange(PtrRecordsAt(ip));
                    related.AddRange(ARecordsAt(ip));
                }
                related.AddRange(RecordsNamed(record.OwnerName));
                related.AddRange(CnamesPointingTo(record.OwnerName));
                break;

            case RecordType.PTR:
                related.AddRange(RecordsNamed(record.Data));
                if (Ipv4.TryToUInt32(record.Address, out uint ptrIp))
                {
                    related.AddRange(ARecordsAt(ptrIp));
                    related.AddRange(PtrRecordsAt(ptrIp));
                }
                break;

            case RecordType.CNAME:
                related.AddRange(RecordsNamed(record.Data));
                related.AddRange(CnamesPointingTo(record.OwnerName));
                break;
        }

        return related
            .Where(r => r.Key != record.Key)
            .DistinctBy(r => r.Key)
            .ToList();
    }

    /// <summary>Neues Inventar mit hinzugefügten und entfernten Einträgen.</summary>
    public DnsInventory With(IEnumerable<DnsRecord>? added = null, IEnumerable<DnsRecord>? removed = null)
    {
        var removedKeys = (removed ?? []).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var records = Records
            .Where(r => !removedKeys.Contains(r.Key))
            .Concat(added ?? [])
            .DistinctBy(r => r.Key)
            .ToList();

        return new DnsInventory(Zones, records);
    }

    private static string Trim(string name) => name.Trim().TrimEnd('.');

    private static void Add<TKey>(Dictionary<TKey, List<DnsRecord>> map, TKey key, DnsRecord record)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(record);
    }
}
