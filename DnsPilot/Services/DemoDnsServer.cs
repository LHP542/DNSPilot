using System.Net;
using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>
/// Frei erfundene DNS-Daten für Bildschirmfotos und Tests. Enthält absichtlich
/// je ein Beispiel für jeden Befund: verwaister PTR, fehlender PTR, CNAME ins
/// Leere, doppelte Adresse, veralteter Eintrag.
///
/// Keine echten Namen und Adressen — Bilder davon landen in der README.
/// </summary>
public sealed class DemoDnsServer : IDnsServer
{
    private readonly List<DnsRecord> _records;
    private readonly List<DnsZone> _zones;

    public DemoDnsServer(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        const string fwd = "musterstadt.beispiel";
        const string rev1 = "10.10.in-addr.arpa";
        const string rev2 = "20.10.in-addr.arpa";

        _zones =
        [
            new DnsZone(fwd, false, ZoneKind.Primary, true),
            new DnsZone("_msdcs." + fwd, false, ZoneKind.Primary, true),
            new DnsZone(rev1, true, ZoneKind.Primary, true),
            new DnsZone(rev2, true, ZoneKind.Primary, true),
        ];

        DnsRecord A(string host, string ip, int? daysAgo = null) =>
            new(fwd, host == fwd ? fwd : $"{host}.{fwd}", RecordType.A, ip, 3600,
                daysAgo is { } d ? now.AddDays(-d) : null);

        DnsRecord Ptr(string ip, string target, string zone, int? daysAgo = null)
        {
            Ipv4.TryParseStrict(ip, out uint value);
            return new DnsRecord(zone, ReverseZone.PtrOwnerName(value), RecordType.PTR, target, 3600,
                daysAgo is { } d ? now.AddDays(-d) : null);
        }

        _records =
        [
            A(fwd, "10.10.0.11"),
            A("dc01", "10.10.0.11"),
            Ptr("10.10.0.11", "dc01." + fwd, rev1),
            A("dc02", "10.10.0.12"),
            Ptr("10.10.0.12", "dc02." + fwd, rev1),
            A("fileserver", "10.10.1.20"),
            Ptr("10.10.1.20", "fileserver." + fwd, rev1),
            A("drucker-eg", "10.10.2.31"),
            Ptr("10.10.2.31", "drucker-eg." + fwd, rev1),
            A("drucker-og1", "10.10.2.32"),
            // Fehlender PTR
            A("kamera-hof", "10.10.2.40"),
            // Doppelte Adresse ohne PTR — unklar, welcher Name gilt
            A("altes-nas", "10.10.1.25"),
            A("nas-neu", "10.10.1.25"),
            // Zusätzlicher Name für den Drucker, der PTR nennt den Hauptnamen
            A("drucker-empfang", "10.10.2.31"),
            // Dynamische Clients, einer davon veraltet
            A("pc-0412", "10.20.5.12", daysAgo: 1),
            Ptr("10.20.5.12", "pc-0412." + fwd, rev2, daysAgo: 1),
            A("pc-0388", "10.20.5.47", daysAgo: 214),
            Ptr("10.20.5.47", "pc-0388." + fwd, rev2, daysAgo: 214),
            A("nb-1107", "10.20.6.8", daysAgo: 3),
            Ptr("10.20.6.8", "nb-1107." + fwd, rev2, daysAgo: 3),
            // PTR wird aktiv registriert, der A-Eintrag fehlt (Gerät außerhalb der Domäne)
            Ptr("10.20.6.30", "thinclient-07." + fwd, rev2, daysAgo: 1),
            // Verwaister PTR — der Host ist längst weg
            Ptr("10.10.2.55", "scanner-keller." + fwd, rev1),
            // PTR zeigt auf einen anderen Host als der A-Eintrag
            A("zeiterfassung", "10.10.3.10"),
            Ptr("10.10.3.10", "terminal-alt." + fwd, rev1),
            // Aliase
            new(fwd, "intranet." + fwd, RecordType.CNAME, "fileserver." + fwd, 3600, null),
            new(fwd, "wiki." + fwd, RecordType.CNAME, "wikiserver." + fwd, 3600, null),
            // Ohne Reverse-Zone
            A("gaeste-wlan", "192.168.50.1"),
        ];
    }

    public string ServerName { get; init; } = "dc01.musterstadt.beispiel";

    internal IReadOnlyList<DnsRecord> Records => _records;

    public Task<DnsServerInfo> ConnectAsync(CancellationToken token) =>
        Task.FromResult(new DnsServerInfo(ServerName, @"MUSTERSTADT\adm.mustermann"));

    public Task<DnsSnapshot> LoadAsync(IProgress<string>? progress, CancellationToken token) =>
        Task.FromResult(new DnsSnapshot(_zones.ToList(), _records.ToList(), DateTime.UtcNow, TimeSpan.FromMilliseconds(420)));

    public Task<IReadOnlyList<DnsRecord>> CreateHostAsync(NewHostRequest request, CancellationToken token)
    {
        var created = new List<DnsRecord>
        {
            new(request.Zone, request.Fqdn, RecordType.A, request.Address.ToString(), request.Ttl, null),
        };

        if (request.ReverseZone is { } rz && Ipv4.TryToUInt32(request.Address, out uint ip))
        {
            created.Add(new DnsRecord(rz, ReverseZone.PtrOwnerName(ip), RecordType.PTR, request.Fqdn, request.Ttl, null));
        }

        _records.AddRange(created);
        return Task.FromResult<IReadOnlyList<DnsRecord>>(created);
    }

    public Task DeleteRecordAsync(DnsRecord record, CancellationToken token)
    {
        if (_records.RemoveAll(r => r.Key == record.Key) == 0)
        {
            throw new DnsAccessException($"{record.OwnerName} existiert nicht mehr.");
        }
        return Task.CompletedTask;
    }

    public void Invalidate()
    {
    }
}

/// <summary>Erfundene Erreichbarkeit passend zu <see cref="DemoDnsServer"/>.</summary>
public sealed class DemoHostProbe(DemoDnsServer server) : IHostProbe
{
    private static readonly HashSet<string> Offline = ["10.10.2.55", "10.20.5.47", "10.10.1.25", "10.10.2.40"];

    public Task<ProbeResult> ProbeAsync(IPAddress address, ProbeOptions options, CancellationToken token)
    {
        string ip = address.ToString();
        return Task.FromResult(Offline.Contains(ip)
            ? new ProbeResult(false, null, null)
            : new ProbeResult(true, ip.StartsWith("10.10.0.", StringComparison.Ordinal) ? "TCP 445" : "ICMP", 2));
    }

    public Task<ResolveResult> ResolveAsync(string name, CancellationToken token)
    {
        // CNAME einen Schritt weit verfolgen, dann die A-Adressen.
        string target = server.Records
            .FirstOrDefault(r => r.Type == RecordType.CNAME && r.OwnerName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Data
            ?? name;
        var addresses = server.Records
            .Where(r => r.Type == RecordType.A && r.OwnerName.Equals(target, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Data)
            .ToList();
        return Task.FromResult(new ResolveResult(addresses, addresses.Count == 0 ? "nicht auflösbar" : null));
    }

    public Task<ResolveResult> ReverseResolveAsync(IPAddress address, CancellationToken token)
    {
        var names = server.Records
            .Where(r => r.Type == RecordType.PTR && Equals(r.Address, address))
            .Select(r => r.Data)
            .Take(1)
            .ToList();
        return Task.FromResult(new ResolveResult(names, names.Count == 0 ? "nicht auflösbar" : null));
    }
}
