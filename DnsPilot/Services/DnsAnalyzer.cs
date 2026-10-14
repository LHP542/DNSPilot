using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>
/// Sucht in einem <see cref="DnsInventory"/> nach Widersprüchen und Altlasten.
/// Arbeitet ausschließlich auf den geladenen Daten — kein Netzwerkzugriff, und
/// damit in Millisekunden fertig und vollständig testbar.
///
/// Was hier steht, ist ein <b>Hinweis</b>, kein Urteil. Ein fehlender PTR kann
/// Absicht sein, ein veralteter Zeitstempel ein Rechner im Urlaub. Die
/// Löschentscheidung trifft der Mensch.
/// </summary>
public sealed class DnsAnalyzer(int staleAfterDays)
{
    /// <summary>
    /// Namen, die das AD selbst für jeden DC anlegt. Dass deren Adressen auch an
    /// den DC-Namen hängen, ist gewollt — ohne diese Ausnahme stünde bei jedem
    /// DC eine Warnung „doppelte IP".
    /// </summary>
    private static readonly string[] AdServiceLabels = ["domaindnszones", "forestdnszones", "gc"];

    /// <summary>Befunde für alle Einträge, nach <see cref="DnsRecord.Key"/>.</summary>
    public Dictionary<string, IReadOnlyList<Finding>> AnalyzeAll(DnsInventory inventory, DateTime nowUtc)
    {
        var result = new Dictionary<string, IReadOnlyList<Finding>>(StringComparer.Ordinal);
        foreach (var record in inventory.Records)
        {
            var findings = Analyze(inventory, record, nowUtc);
            if (findings.Count > 0) result[record.Key] = findings;
        }
        return result;
    }

    /// <summary>Befunde für einen einzelnen Eintrag.</summary>
    public IReadOnlyList<Finding> Analyze(DnsInventory inventory, DnsRecord record, DateTime nowUtc)
    {
        var findings = new List<Finding>();

        switch (record.Type)
        {
            case RecordType.A:
                CheckA(inventory, record, findings);
                break;
            case RecordType.PTR:
                CheckPtr(inventory, record, nowUtc, findings);
                break;
            case RecordType.CNAME:
                CheckCname(inventory, record, findings);
                break;
        }

        CheckStale(record, nowUtc, findings);
        CheckShadowed(inventory, record, findings);
        return findings;
    }

    private static void CheckA(DnsInventory inventory, DnsRecord record, List<Finding> findings)
    {
        if (!Ipv4.TryToUInt32(record.Address, out uint ip)) return;

        var reverseZone = inventory.ReverseZoneFor(ip);
        var ptrs = inventory.PtrRecordsAt(ip);
        bool infrastructure = IsZoneApexOrAdService(record, inventory);

        // Hauptnamen: Namen, auf die ein PTR dieser Adresse zeigt UND die
        // selbst einen A-Eintrag auf genau diese Adresse haben. Gibt es einen,
        // ist das DNS für diese Adresse in sich stimmig, und weitere Namen
        // sind zusätzliche Namen (Alias) — gewollt oder vergessen, das kann
        // nur ein Mensch entscheiden. Beispiel vom ersten echten Lauf: zwei
        // statische Namen für einen Server, dessen PTR seinen Computernamen nennt.
        var aAtIp = inventory.ARecordsAt(ip).Where(a => !IsZoneApexOrAdService(a, inventory)).ToList();
        var mainNames = ptrs
            .Where(p => aAtIp.Any(a => SameName(a.OwnerName, p.Data)))
            .Select(p => p.Data)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool isMain = mainNames.Any(n => SameName(n, record.OwnerName));

        if (reverseZone is null)
        {
            findings.Add(new Finding(FindingKind.NoReverseZone, Severity.Info,
                $"Keine Reverse-Zone für {record.Data} — ein PTR ist nicht möglich."));
        }
        else if (ptrs.Count == 0)
        {
            findings.Add(new Finding(FindingKind.MissingPtr, Severity.Warning,
                $"PTR fehlt in {reverseZone.Name}."));
        }
        else if (!infrastructure && !ptrs.Any(p => SameName(p.Data, record.OwnerName)))
        {
            if (mainNames.Count > 0)
            {
                findings.Add(new Finding(FindingKind.AliasName, Severity.Info,
                    $"Weiterer Name für {record.Data}; der PTR nennt den Hauptnamen {string.Join(", ", mainNames)}. "
                    + "Gewollter Alias oder alter Name? Falls nicht mehr gebraucht, löschen."));
            }
            else
            {
                findings.Add(Mismatch(record, ptrs, $"PTR zeigt auf {string.Join(", ", ptrs.Select(p => p.Data))}"));
            }
        }

        if (infrastructure) return;

        var others = aAtIp
            .Where(a => !SameName(a.OwnerName, record.OwnerName))
            .Select(a => a.OwnerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (others.Count == 0) return;

        if (mainNames.Count == 0)
        {
            // Mehrere Namen, aber kein PTR, der einen davon bestätigt: hier ist
            // unklar, welcher Name der richtige ist.
            findings.Add(new Finding(FindingKind.DuplicateIp, Severity.Warning,
                $"Adresse auch vergeben an: {string.Join(", ", others)}."));
        }
        else if (isMain)
        {
            findings.Add(new Finding(FindingKind.AliasName, Severity.Info,
                $"Weitere Namen auf dieser Adresse: {string.Join(", ", others)}."));
        }
        // Für Nicht-Hauptnamen steht der Hinweis schon oben — nicht doppelt melden.
    }

    private void CheckPtr(DnsInventory inventory, DnsRecord record, DateTime nowUtc, List<Finding> findings)
    {
        string target = record.Data;

        if (inventory.ForwardZoneFor(target) is null)
        {
            findings.Add(new Finding(FindingKind.PtrExternal, Severity.Info,
                $"Ziel {target} liegt außerhalb der Zonen dieses Servers."));
            return;
        }

        var targetRecords = inventory.RecordsNamed(target);
        var targetA = targetRecords.Where(r => r.Type == RecordType.A).ToList();

        // Ein CNAME als PTR-Ziel ist unsauber, aber kein verwaister Eintrag.
        if (targetA.Count == 0 && targetRecords.Any(r => r.Type == RecordType.CNAME)) return;

        if (targetA.Count == 0)
        {
            findings.Add(MissingTarget(record, target, targetRecords.Count == 0, nowUtc));
            return;
        }

        string? ownIp = record.Address?.ToString();
        if (ownIp is not null && targetA.All(a => a.Data != ownIp))
        {
            findings.Add(Mismatch(record, targetA,
                $"{target} hat die Adresse {string.Join(", ", targetA.Select(a => a.Data))}, nicht {ownIp}"));
        }
    }

    /// <summary>
    /// Widerspruch zwischen PTR und A. Sind <b>alle</b> Beteiligten dynamisch,
    /// ist das der Normalfall eines Geräts, das das Netz gewechselt hat
    /// (Notebook: WLAN, LAN, VPN) — der alte Eintrag verschwindet beim
    /// Aufräumen des Servers. Das ist
    /// eine Info, keine Warnung, sonst verstopfen hunderte Notebooks die Liste
    /// „Nur Auffälligkeiten". Ist einer davon statisch, räumt ihn niemand weg:
    /// dann ist es ein Pflegefehler.
    /// </summary>
    private static Finding Mismatch(DnsRecord record, IEnumerable<DnsRecord> counterparts, string text) =>
        !record.IsStatic && counterparts.All(c => !c.IsStatic)
            ? new Finding(FindingKind.PtrMismatch, Severity.Info,
                $"Adresswechsel: {text}. Beide dynamisch — der alte Eintrag verschwindet beim Aufräumen des Servers.")
            : new Finding(FindingKind.PtrMismatch, Severity.Warning,
                $"{text}. Mindestens ein Eintrag ist statisch — das räumt niemand automatisch auf.");

    /// <summary>
    /// PTR ohne A-Eintrag beim Ziel. Zwei Fälle, die gleich aussehen und
    /// entgegengesetzt behandelt werden müssen:
    /// <list type="bullet">
    ///   <item><b>PTR dynamisch und frisch:</b> Jemand registriert ihn aktiv
    ///   (typisch der DHCP-Server), nur der A-Eintrag kommt nicht zustande —
    ///   etwa weil ein Gerät außerhalb der Domäne in einer „Secure"-Zone nicht
    ///   schreiben darf. Den PTR zu löschen wäre falsch: er käme morgen wieder.
    ///   Das Problem ist der fehlende A-Eintrag.</item>
    ///   <item><b>PTR statisch oder lange nicht erneuert:</b> ein echter
    ///   Überrest, der Host ist weg. Aufräum-Kandidat.</item>
    /// </list>
    /// Gefunden beim ersten echten Lauf (2026-10-13): Thin Clients mit frischem PTR,
    /// ohne A-Eintrag — die erste Fassung meldete „existiert nicht mehr".
    /// </summary>
    private Finding MissingTarget(DnsRecord ptr, string target, bool nameMissing, DateTime nowUtc)
    {
        string what = nameMissing ? $"{target} gibt es nicht" : $"{target} hat keinen A-Eintrag";

        if (ptr.TimestampUtc is { } stamp && (nowUtc - stamp).TotalDays < staleAfterDays)
        {
            return new Finding(FindingKind.MissingA, Severity.Warning,
                $"A-Eintrag fehlt: {what}, der PTR wird aber aktiv registriert (zuletzt {stamp.ToLocalTime():dd.MM.yyyy}). "
                + "Nicht den PTR löschen, sondern klären, warum der A-Eintrag nicht registriert wird.");
        }

        string why = ptr.TimestampUtc is null ? "PTR ist statisch" : "PTR wird nicht mehr erneuert";
        return new Finding(FindingKind.PtrOrphan, Severity.Error, $"Verwaist: {what} ({why}).");
    }

    private static void CheckCname(DnsInventory inventory, DnsRecord record, List<Finding> findings)
    {
        // Ziele außerhalb der eigenen Zonen (z. B. ein Cloud-Dienst) lassen sich
        // hier nicht beurteilen — dafür müsste man sie auflösen, und das macht
        // die Erreichbarkeitsprüfung.
        if (inventory.ForwardZoneFor(record.Data) is null) return;

        if (inventory.RecordsNamed(record.Data).Count == 0)
        {
            findings.Add(new Finding(FindingKind.CnameDangling, Severity.Error,
                $"Ziel {record.Data} existiert nicht."));
        }
    }

    private static void CheckShadowed(DnsInventory inventory, DnsRecord record, List<Finding> findings)
    {
        var owning = inventory.MostSpecificZoneFor(record.OwnerName);
        if (owning is null || owning.Name.Equals(record.Zone, StringComparison.OrdinalIgnoreCase)) return;

        findings.Add(new Finding(FindingKind.Shadowed, Severity.Warning,
            $"Verdeckt: liegt in {record.Zone}, gefragt wird aber die Unterzone {owning.Name} — Clients sehen diesen Eintrag nicht."));
    }

    private void CheckStale(DnsRecord record, DateTime nowUtc, List<Finding> findings)
    {
        if (record.TimestampUtc is not { } stamp) return;

        int days = (int)(nowUtc - stamp).TotalDays;
        if (days >= staleAfterDays)
        {
            findings.Add(new Finding(FindingKind.Stale, Severity.Warning,
                $"Seit {days} Tagen nicht mehr registriert (letzte Registrierung {stamp.ToLocalTime():dd.MM.yyyy})."));
        }
    }

    /// <summary>
    /// Einträge an der Zonenspitze („lhp.intern" → DC-Adresse) und die
    /// AD-Dienstnamen gehören dem Domänencontroller und sind keine Hosts.
    /// </summary>
    internal static bool IsZoneApexOrAdService(DnsRecord record, DnsInventory inventory)
    {
        if (record.OwnerName.Equals(record.Zone, StringComparison.OrdinalIgnoreCase)) return true;

        string firstLabel = record.OwnerName.Split('.')[0];
        return AdServiceLabels.Contains(firstLabel, StringComparer.OrdinalIgnoreCase)
            || inventory.ForwardZoneFor(record.OwnerName)?.Name.StartsWith("_msdcs.", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool SameName(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}
