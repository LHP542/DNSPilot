using System.Globalization;

namespace DnsPilot.Services;

/// <summary>Art der Eingabe im Suchfeld.</summary>
public enum SearchKind
{
    /// <summary>Leeres Feld — alles anzeigen.</summary>
    All,

    /// <summary>Freitext: Teil eines Namens, einer Adresse oder eines Ziels.</summary>
    Text,

    /// <summary>Eine Adresse, ein Netz oder ein Adressbereich.</summary>
    Range,
}

/// <summary>
/// Deutet die Eingabe im Suchfeld. Erkannt werden:
/// <list type="bullet">
///   <item><c>172.16.1.11</c> — eine Adresse</item>
///   <item><c>172.16.1.0/24</c> — ein Netz (die Adresse wird auf die Netzgrenze gezogen)</item>
///   <item><c>172.16.1.10-50</c> oder <c>172.16.1.10-172.16.1.50</c> — ein Bereich</item>
///   <item><c>172.16.1.*</c> oder <c>172.16.*</c> — Platzhalter für ganze Oktette</item>
/// </list>
/// Alles andere ist Freitext. Ein angefangenes <c>172.16.1</c> bleibt bewusst
/// Freitext: es findet per Teilstring dieselben Einträge, ohne dass beim
/// Tippen ein falsches Netz gerechnet wird.
/// </summary>
public sealed record SearchQuery(SearchKind Kind, string Text, IpRange? Range)
{
    public static SearchQuery Parse(string? input)
    {
        string text = input?.Trim() ?? string.Empty;
        if (text.Length == 0) return new SearchQuery(SearchKind.All, text, null);

        return TryParseRange(text, out var range)
            ? new SearchQuery(SearchKind.Range, text, range)
            : new SearchQuery(SearchKind.Text, text, null);
    }

    internal static bool TryParseRange(string text, out IpRange range)
    {
        range = default;
        text = text.Replace(" ", string.Empty, StringComparison.Ordinal);

        // Netz: a.b.c.d/nn
        int slash = text.IndexOf('/');
        if (slash > 0)
        {
            if (!Ipv4.TryParseStrict(text[..slash], out uint address)) return false;
            if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix)
                || prefix is < 8 or > 32)
            {
                // Unter /8 sind es mehr als 16 Millionen Adressen — das ist
                // kein Netz, das man in einer Tabelle durchsieht, sondern ein
                // Tippfehler.
                return false;
            }

            range = IpRange.FromCidr(address, prefix);
            return true;
        }

        // Platzhalter: a.b.c.* / a.b.* / a.*
        if (text.EndsWith(".*", StringComparison.Ordinal))
        {
            string[] head = text[..^2].Split('.');
            if (head.Length is < 1 or > 3) return false;

            uint value = 0;
            foreach (string part in head)
            {
                if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out byte octet)) return false;
                value = (value << 8) | octet;
            }

            int prefix = head.Length * 8;
            range = IpRange.FromCidr(value << (32 - prefix), prefix);
            return true;
        }

        // Bereich: a.b.c.d-e  oder  a.b.c.d-w.x.y.z
        int dash = text.IndexOf('-');
        if (dash > 0)
        {
            if (!Ipv4.TryParseStrict(text[..dash], out uint first)) return false;
            string tail = text[(dash + 1)..];

            uint last;
            if (Ipv4.TryParseStrict(tail, out uint full))
            {
                last = full;
            }
            else if (byte.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out byte lastOctet))
            {
                last = (first & 0xFFFFFF00u) | lastOctet;
            }
            else
            {
                return false;
            }

            if (last < first) return false;
            range = new IpRange(first, last);
            return true;
        }

        // Einzelne Adresse
        if (Ipv4.TryParseStrict(text, out uint single))
        {
            range = new IpRange(single, single);
            return true;
        }

        return false;
    }
}
