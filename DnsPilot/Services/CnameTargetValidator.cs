using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>Ergebnis der Prüfung eines neuen CNAME-Ziels.</summary>
/// <param name="Fqdn">Das vollständige neue Ziel, oder null, wenn die Eingabe unbrauchbar ist.</param>
/// <param name="Errors">Blockierende Fehler — so wird nicht umgebogen.</param>
/// <param name="Warnings">Hinweise, die bestätigt werden müssen.</param>
/// <param name="Info">Was hinter dem Ziel steht (z. B. „A 10.10.1.20").</param>
public sealed record CnameTargetCheck(string? Fqdn, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string? Info)
{
    public bool IsValid => Errors.Count == 0 && Fqdn is not null;
}

/// <summary>
/// Prüft beim Tippen, ob ein neues Ziel für einen Alias taugt: gültiger Name,
/// nicht der Alias selbst, keine Schleife, und — soweit es in den eigenen
/// Zonen liegt — ob es den Namen überhaupt gibt. Sonst biegt man einen Alias
/// ins Leere auf das nächste Leere um.
/// </summary>
public static class CnameTargetValidator
{
    /// <summary>Ab so vielen Schritten gilt eine CNAME-Kette als Schleife.</summary>
    private const int MaxChain = 8;

    public static CnameTargetCheck Check(DnsInventory inventory, DnsRecord alias, string? input)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        string text = input?.Trim().TrimEnd('.') ?? string.Empty;
        if (text.Length == 0) return new CnameTargetCheck(null, ["Bitte ein neues Ziel eingeben."], warnings, null);

        // Ohne Punkt ist ein Name in der Zone des Alias gemeint („fileserver").
        string zone = inventory.ForwardZoneFor(alias.OwnerName)?.Name ?? alias.Zone;
        string fqdn = text.Contains('.') ? text : $"{text}.{zone}";

        if (!NewHostValidator.IsValidHostLabel(fqdn))
        {
            return new CnameTargetCheck(null,
                ["Kein gültiger Name: nur Buchstaben, Ziffern, Bindestriche und Punkte (je Teil höchstens 63 Zeichen)."],
                warnings, null);
        }

        if (Same(fqdn, alias.OwnerName)) errors.Add("Ein Alias kann nicht auf sich selbst zeigen.");
        else if (Same(fqdn, alias.Data)) errors.Add("Das ist bereits das aktuelle Ziel.");

        if (errors.Count > 0) return new CnameTargetCheck(fqdn, errors, warnings, null);

        if (inventory.ForwardZoneFor(fqdn) is null)
        {
            return new CnameTargetCheck(fqdn, errors, warnings,
                $"{fqdn} liegt außerhalb der Zonen dieses Servers — ob es den Namen gibt, prüft DNS-Pilot nicht.");
        }

        var records = inventory.RecordsNamed(fqdn);
        if (records.Count == 0)
        {
            warnings.Add($"{fqdn} gibt es in den eigenen Zonen nicht — der Alias zeigte weiter ins Leere.");
            return new CnameTargetCheck(fqdn, errors, warnings, null);
        }

        if (records.FirstOrDefault(r => r.Type == RecordType.CNAME) is { } next)
        {
            if (LeadsTo(inventory, next.Data, alias.OwnerName))
            {
                errors.Add($"{fqdn} führt über Aliase zurück zu {alias.OwnerName} — das wäre eine Schleife.");
                return new CnameTargetCheck(fqdn, errors, warnings, null);
            }

            warnings.Add($"{fqdn} ist selbst ein Alias (→ {next.Data}). Ketten funktionieren, kosten aber bei jeder "
                + "Auflösung einen Schritt mehr — besser direkt auf den Host zeigen.");
        }

        string info = string.Join(", ", records.Select(r => $"{r.Type} {r.Data}"));
        return new CnameTargetCheck(fqdn, errors, warnings, $"{fqdn} → {info}");
    }

    private static bool LeadsTo(DnsInventory inventory, string start, string alias)
    {
        string current = start;
        for (int i = 0; i < MaxChain; i++)
        {
            if (Same(current, alias)) return true;
            var cname = inventory.RecordsNamed(current).FirstOrDefault(r => r.Type == RecordType.CNAME);
            if (cname is null) return false;
            current = cname.Data;
        }
        return true;
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}
