namespace DnsPilot.Models;

/// <summary>Ergebnis eines erfolgreichen Verbindungsaufbaus.</summary>
/// <param name="ServerName">Name, den der DNS-Server selbst meldet (FQDN).</param>
/// <param name="ConnectedAs">Konto, unter dem zugegriffen wird.</param>
public sealed record DnsServerInfo(string ServerName, string ConnectedAs)
{
    public string StatusText => $"{ServerName} · als {ConnectedAs}";
}

/// <summary>Alles, was beim Laden vom Server kommt.</summary>
public sealed record DnsSnapshot(
    IReadOnlyList<DnsZone> Zones,
    IReadOnlyList<DnsRecord> Records,
    DateTime LoadedAtUtc,
    TimeSpan Duration);

/// <summary>Auftrag zum Anlegen eines Hosts.</summary>
/// <param name="Zone">Forward-Zone.</param>
/// <param name="HostLabel">Name relativ zur Zone, z. B. <c>drucker-3og</c>.</param>
/// <param name="Address">IPv4-Adresse.</param>
/// <param name="ReverseZone">Reverse-Zone für den PTR, oder null = keinen PTR anlegen.</param>
/// <param name="Ttl">Gültigkeitsdauer in Sekunden.</param>
public sealed record NewHostRequest(
    string Zone,
    string HostLabel,
    System.Net.IPAddress Address,
    string? ReverseZone,
    uint Ttl)
{
    public string Fqdn => $"{HostLabel}.{Zone}";
}
