using System.Text.RegularExpressions;
using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>Ergebnis der Prüfung eines neuen Hosts.</summary>
/// <param name="Errors">Blockierende Fehler — so wird nicht angelegt.</param>
/// <param name="Warnings">Hinweise, die man bestätigen kann (Adresse schon vergeben …).</param>
public sealed record NewHostCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Prüft einen neuen Host gegen die DNS-Regeln und gegen das, was schon im
/// Inventar steht. Der Server würde vieles davon auch ablehnen — aber erst nach
/// dem Klick, und eine doppelt vergebene Adresse lehnt er gar nicht ab.
/// </summary>
public static partial class NewHostValidator
{
    public static NewHostCheck Check(DnsInventory inventory, string? zone, string? hostLabel, string? address)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        string label = hostLabel?.Trim().TrimEnd('.') ?? string.Empty;
        var forwardZone = inventory.ForwardZones.FirstOrDefault(z => z.Name.Equals(zone, StringComparison.OrdinalIgnoreCase));

        if (forwardZone is null)
        {
            errors.Add("Bitte eine Zone auswählen.");
        }
        else if (!forwardZone.IsWritable)
        {
            errors.Add($"Die Zone {forwardZone.Name} ist auf diesem Server nicht beschreibbar ({forwardZone.ZoneType}).");
        }

        if (label.Length == 0)
        {
            errors.Add("Bitte einen Namen eingeben.");
        }
        else if (!IsValidHostLabel(label))
        {
            errors.Add("Der Name darf nur Buchstaben, Ziffern und Bindestriche enthalten (je Teil höchstens 63 Zeichen, kein Bindestrich am Anfang oder Ende).");
        }

        if (!Ipv4.TryParseStrict(address, out uint ip))
        {
            errors.Add("Bitte eine gültige IPv4-Adresse eingeben (z. B. 172.16.1.50).");
        }

        if (errors.Count > 0) return new NewHostCheck(errors, warnings);

        string fqdn = $"{label}.{forwardZone!.Name}";
        var existing = inventory.RecordsNamed(fqdn);
        if (existing.Count > 0)
        {
            warnings.Add($"{fqdn} existiert bereits: {string.Join(", ", existing.Select(r => $"{r.Type} {r.Data}"))}.");
        }

        var sameIp = inventory.ARecordsAt(ip);
        if (sameIp.Count > 0)
        {
            warnings.Add($"Die Adresse ist schon vergeben an: {string.Join(", ", sameIp.Select(r => r.OwnerName))}.");
        }

        var ptrs = inventory.PtrRecordsAt(ip);
        if (ptrs.Count > 0)
        {
            warnings.Add($"Für die Adresse gibt es schon einen PTR auf {string.Join(", ", ptrs.Select(r => r.Data))}.");
        }

        if (inventory.ReverseZoneFor(ip) is null)
        {
            warnings.Add("Für dieses Netz gibt es keine Reverse-Zone — es wird nur der A-Eintrag angelegt.");
        }

        return new NewHostCheck(errors, warnings);
    }

    /// <summary>
    /// Ein Hostname relativ zur Zone: ein oder mehrere Labels nach RFC 1123.
    /// Unterstriche sind für Hosts nicht erlaubt (nur für Dienstnamen wie
    /// <c>_ldap</c>) — Windows nimmt sie an, viele Clients lösen sie nicht auf.
    /// </summary>
    public static bool IsValidHostLabel(string label) =>
        label.Length <= 200 && label.Split('.').All(part => LabelPattern().IsMatch(part));

    [GeneratedRegex("^(?!-)[A-Za-z0-9-]{1,63}(?<!-)$")]
    private static partial Regex LabelPattern();
}
