using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.Tests;

/// <summary>Kleine Bausteine, um Inventare für Tests zusammenzustecken.</summary>
internal static class TestData
{
    public const string Fwd = "lhp.test";
    public const string Rev = "1.16.172.in-addr.arpa";

    public static DnsZone ForwardZone(string name = Fwd) => new(name, false, ZoneKind.Primary, true);

    public static DnsZone ReverseZoneOf(string name = Rev) => new(name, true, ZoneKind.Primary, true);

    public static DnsRecord A(string host, string ip, DateTime? stamp = null, string zone = Fwd) =>
        new(zone, host.Contains('.') ? host : $"{host}.{zone}", RecordType.A, ip, 3600, stamp);

    public static DnsRecord Ptr(string ip, string target, string zone = Rev, DateTime? stamp = null)
    {
        Ipv4.TryParseStrict(ip, out uint value);
        return new DnsRecord(zone, ReverseZone.PtrOwnerName(value), RecordType.PTR,
            target.Contains('.') ? target : $"{target}.{Fwd}", 3600, stamp);
    }

    public static DnsRecord Cname(string alias, string target) =>
        new(Fwd, $"{alias}.{Fwd}", RecordType.CNAME, target.Contains('.') ? target : $"{target}.{Fwd}", 3600, null);

    public static DnsInventory Inventory(params DnsRecord[] records) =>
        new([ForwardZone(), ReverseZoneOf()], records);
}
