using System.Globalization;
using System.Net;

namespace DnsPilot.Services;

/// <summary>
/// Rechnet zwischen IPv4-Adressen und den Namen in <c>in-addr.arpa</c>.
///
/// Eine Reverse-Zone <c>1.16.172.in-addr.arpa</c> deckt das Netz
/// 172.16.1.0/24 ab, der PTR für 172.16.1.11 heißt
/// <c>11.1.16.172.in-addr.arpa</c>. Klassenlose Delegationen nach RFC 2317
/// (<c>0-25.1.16.172…</c>) werden nicht als Netz erkannt — sie kommen in der
/// Praxis selten vor und würden hier nur falsche Zuordnungen erzeugen.
/// </summary>
public static class ReverseZone
{
    private const string Suffix = ".in-addr.arpa";

    /// <summary>Ist der Name eine IPv4-Reverse-Zone (oder ein Name darin)?</summary>
    public static bool IsIpv4Reverse(string name) =>
        Normalize(name).EndsWith(Suffix, StringComparison.Ordinal) || Normalize(name) == "in-addr.arpa";

    /// <summary>Welches Netz deckt eine Reverse-Zone ab?</summary>
    public static bool TryGetNetwork(string zoneName, out IpRange network)
    {
        network = default;
        if (!TryGetLabels(zoneName, out var labels) || labels.Count is 0 or > 4) return false;

        uint value = 0;
        // Die Labels stehen umgekehrt: 1.16.172 → 172.16.1
        for (int i = labels.Count - 1; i >= 0; i--)
        {
            value = (value << 8) | labels[i];
        }

        int prefix = labels.Count * 8;
        network = IpRange.FromCidr(value << (32 - prefix), prefix);
        return true;
    }

    /// <summary>Rechnet aus einem PTR-Namen die Adresse zurück.</summary>
    public static bool TryParsePtrOwner(string ownerName, out IPAddress address)
    {
        address = IPAddress.None;
        if (!TryGetLabels(ownerName, out var labels) || labels.Count != 4) return false;

        address = new IPAddress([labels[3], labels[2], labels[1], labels[0]]);
        return true;
    }

    /// <summary>Der PTR-Name zu einer Adresse, z. B. <c>11.1.16.172.in-addr.arpa</c>.</summary>
    public static string PtrOwnerName(uint address) =>
        $"{address & 0xFF}.{(address >> 8) & 0xFF}.{(address >> 16) & 0xFF}.{address >> 24}{Suffix}";

    private static bool TryGetLabels(string name, out List<byte> labels)
    {
        labels = [];
        string n = Normalize(name);
        if (!n.EndsWith(Suffix, StringComparison.Ordinal)) return false;

        string head = n[..^Suffix.Length];
        if (head.Length == 0) return false;

        foreach (string part in head.Split('.'))
        {
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out byte octet)) return false;
            // „01" ist kein gültiges Label einer Reverse-Zone, sondern ein Tippfehler.
            if (part.Length > 1 && part[0] == '0') return false;
            labels.Add(octet);
        }
        return true;
    }

    private static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();
}
