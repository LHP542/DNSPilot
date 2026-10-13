using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace DnsPilot.Services;

/// <summary>
/// Ein zusammenhängender IPv4-Bereich, inklusive beider Grenzen.
/// </summary>
/// <param name="First">Erste Adresse als Zahl (Netzwerk-Bytefolge ausgewertet).</param>
/// <param name="Last">Letzte Adresse.</param>
/// <param name="PrefixLength">Präfixlänge, wenn der Bereich ein CIDR-Netz ist, sonst null.</param>
public readonly record struct IpRange(uint First, uint Last, int? PrefixLength = null)
{
    /// <summary>Anzahl Adressen im Bereich.</summary>
    public long Count => (long)Last - First + 1;

    public bool Contains(uint address) => address >= First && address <= Last;

    public bool Contains(IPAddress address) =>
        Ipv4.TryToUInt32(address, out uint value) && Contains(value);

    /// <summary>
    /// Die Adressen, die man vergeben kann: in einem Netz bis /30 ohne
    /// Netz- und Broadcastadresse, sonst alle.
    /// </summary>
    public IEnumerable<uint> UsableAddresses()
    {
        uint first = First, last = Last;
        if (PrefixLength is <= 30)
        {
            first++;
            last--;
        }

        for (ulong a = first; a <= last; a++) yield return (uint)a;
    }

    /// <summary>Ein einzelnes Netz aus Adresse und Präfix; die Adresse wird auf die Netzgrenze gezogen.</summary>
    public static IpRange FromCidr(uint address, int prefixLength)
    {
        if (prefixLength is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefixLength));

        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        uint network = address & mask;
        return new IpRange(network, network | ~mask, prefixLength);
    }

    public override string ToString() => PrefixLength is { } p
        ? $"{Ipv4.ToText(First)}/{p}"
        : First == Last ? Ipv4.ToText(First) : $"{Ipv4.ToText(First)} – {Ipv4.ToText(Last)}";
}

/// <summary>Umrechnung zwischen <see cref="IPAddress"/> und Zahl.</summary>
public static class Ipv4
{
    public static bool TryToUInt32(IPAddress? address, out uint value)
    {
        value = 0;
        if (address is null) return false;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out _)) return false;
        value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return true;
    }

    public static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    public static string ToText(uint value) =>
        $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    /// <summary>
    /// Strenge IPv4-Erkennung. <see cref="IPAddress.TryParse(string, out IPAddress)"/>
    /// nimmt auch „10" als 0.0.0.10 und „10.1" als 10.0.0.1 — für ein Suchfeld,
    /// in dem jemand eine Zahl tippt, die Teil eines Namens ist, ist das falsch.
    /// </summary>
    public static bool TryParseStrict(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;

        foreach (string part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)) return false;
            int octet = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            if (octet > 255) return false;
            value = (value << 8) | (uint)octet;
        }
        return true;
    }
}
