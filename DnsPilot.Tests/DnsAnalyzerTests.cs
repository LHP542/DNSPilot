using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;
using static DnsPilot.Tests.TestData;

namespace DnsPilot.Tests;

/// <summary>Befunde: PTR-Abgleich, verwaiste Einträge, doppelte Adressen, Alter.</summary>
public class DnsAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private readonly DnsAnalyzer _analyzer = new(staleAfterDays: 30);

    private IReadOnlyList<FindingKind> Kinds(DnsInventory inv, DnsRecord record) =>
        _analyzer.Analyze(inv, record, Now).Select(f => f.Kind).ToList();

    [Fact]
    public void Sauberer_Host_mit_passendem_PTR_hat_keine_Befunde()
    {
        var a = A("drucker", "172.16.1.20");
        var ptr = Ptr("172.16.1.20", "drucker");
        var inv = Inventory(a, ptr);

        Kinds(inv, a).Should().BeEmpty();
        Kinds(inv, ptr).Should().BeEmpty();
    }

    [Fact]
    public void Fehlender_PTR_wird_gemeldet()
    {
        var a = A("kamera", "172.16.1.40");
        Kinds(Inventory(a), a).Should().Equal(FindingKind.MissingPtr);
    }

    [Fact]
    public void Ohne_Reverse_Zone_ist_der_fehlende_PTR_nur_eine_Info()
    {
        var a = A("gast", "192.168.50.1");
        var findings = _analyzer.Analyze(Inventory(a), a, Now);

        findings.Should().ContainSingle().Which.Should().Match<Finding>(
            f => f.Kind == FindingKind.NoReverseZone && f.Severity == Severity.Info);
    }

    [Fact]
    public void PTR_auf_einen_verschwundenen_Host_ist_ein_Fehler()
    {
        var ptr = Ptr("172.16.1.55", "scanner-keller");
        var findings = _analyzer.Analyze(Inventory(ptr), ptr, Now);

        findings.Should().ContainSingle().Which.Should().Match<Finding>(
            f => f.Kind == FindingKind.PtrOrphan && f.Severity == Severity.Error);
    }

    [Fact]
    public void Verwaister_statischer_PTR_sagt_warum()
    {
        var ptr = Ptr("172.16.1.55", "scanner-keller");

        _analyzer.Analyze(Inventory(ptr), ptr, Now).Single().Text
            .Should().Contain("gibt es nicht").And.Contain("statisch").And.NotContain("nicht mehr");
    }

    [Fact]
    public void Frisch_registrierter_PTR_ohne_A_ist_ein_fehlender_A_Eintrag_kein_verwaister_PTR()
    {
        // Fall aus der Praxis: Thin Clients, PTR gestern erneuert, A gibt es nicht.
        // Den PTR zu löschen wäre falsch — der DHCP-Server legt ihn wieder an.
        var ptr = Ptr("172.16.1.221", "THIN-0653", stamp: Now.AddDays(-1));

        var finding = _analyzer.Analyze(Inventory(ptr), ptr, Now).Should().ContainSingle().Subject;

        finding.Kind.Should().Be(FindingKind.MissingA);
        finding.Severity.Should().Be(Severity.Warning);
        finding.Text.Should().Contain("Nicht den PTR löschen");
    }

    [Fact]
    public void Lange_nicht_erneuerter_PTR_ohne_A_ist_verwaist()
    {
        var ptr = Ptr("172.16.1.222", "alter-client", stamp: Now.AddDays(-90));

        var kinds = Kinds(Inventory(ptr), ptr);

        kinds.Should().Contain(FindingKind.PtrOrphan).And.Contain(FindingKind.Stale).And.NotContain(FindingKind.MissingA);
        _analyzer.Analyze(Inventory(ptr), ptr, Now).First(f => f.Kind == FindingKind.PtrOrphan).Text
            .Should().Contain("nicht mehr erneuert");
    }

    [Theory]
    [InlineData(29, FindingKind.MissingA)]
    [InlineData(30, FindingKind.PtrOrphan)]
    public void Die_Grenze_ist_dieselbe_wie_bei_veralteten_Eintraegen(int daysAgo, FindingKind expected)
    {
        // Dieselbe Schwelle wie „veraltet" — sonst gäbe es PTRs, die zugleich
        // „wird aktiv registriert" und „seit 30 Tagen nicht registriert" hießen.
        var ptr = Ptr("172.16.1.223", "grenzfall", stamp: Now.AddDays(-daysAgo));

        Kinds(Inventory(ptr), ptr).Should().Contain(expected);
    }

    [Fact]
    public void Ziel_mit_nur_AAAA_hat_keinen_A_Eintrag()
    {
        var aaaa = new DnsRecord(Fwd, $"nur-v6.{Fwd}", RecordType.AAAA, "fe80::1", 3600, null);
        var ptr = Ptr("172.16.1.224", "nur-v6");

        _analyzer.Analyze(Inventory(aaaa, ptr), ptr, Now).Single().Text.Should().Contain("hat keinen A-Eintrag");
    }

    [Fact]
    public void PTR_und_A_widersprechen_sich_in_beide_Richtungen()
    {
        var a = A("zeiterfassung", "172.16.1.10");
        var ptr = Ptr("172.16.1.10", "terminal");
        var terminal = A("terminal", "172.16.1.11");
        var inv = Inventory(a, ptr, terminal, Ptr("172.16.1.11", "terminal"));

        Kinds(inv, a).Should().Contain(FindingKind.PtrMismatch);
        Kinds(inv, ptr).Should().Contain(FindingKind.PtrMismatch, "terminal hat .11, der PTR liegt auf .10");
    }

    [Fact]
    public void Adresswechsel_eines_dynamischen_Geraets_ist_nur_eine_Info()
    {
        // Fall aus der Praxis: Notebook NB-0623, PTR auf der alten WLAN-Adresse von
        // heute, A schon auf der neuen. Erledigt sich beim Aufräumen.
        var a = A("NB-0623", "172.16.1.157", stamp: Now.AddHours(-2));
        var oldPtr = Ptr("172.16.1.10", "NB-0623", stamp: Now.AddHours(-6));
        var inv = Inventory(a, oldPtr, Ptr("172.16.1.157", "NB-0623", stamp: Now.AddHours(-2)));

        var finding = _analyzer.Analyze(inv, oldPtr, Now).Should().ContainSingle().Subject;

        finding.Kind.Should().Be(FindingKind.PtrMismatch);
        finding.Severity.Should().Be(Severity.Info);
        finding.Text.Should().StartWith("Adresswechsel");
    }

    [Fact]
    public void Widerspruch_mit_einem_statischen_Eintrag_bleibt_eine_Warnung()
    {
        var a = A("zeiterfassung", "172.16.1.10");                                   // statisch
        var ptr = Ptr("172.16.1.10", "terminal", stamp: Now.AddDays(-1));           // dynamisch
        var terminal = A("terminal", "172.16.1.11", stamp: Now.AddDays(-1));
        var inv = Inventory(a, ptr, terminal, Ptr("172.16.1.11", "terminal", stamp: Now.AddDays(-1)));

        _analyzer.Analyze(inv, a, Now).Single(f => f.Kind == FindingKind.PtrMismatch).Severity.Should().Be(Severity.Warning);
        _analyzer.Analyze(inv, ptr, Now).Single(f => f.Kind == FindingKind.PtrMismatch).Severity
            .Should().Be(Severity.Info, "PTR und das A von terminal sind beide dynamisch");
    }

    [Fact]
    public void PTR_mit_Ziel_ausserhalb_der_Zonen_ist_nur_eine_Info()
    {
        var ptr = Ptr("172.16.1.70", "host.extern.example");
        _analyzer.Analyze(Inventory(ptr), ptr, Now).Should().ContainSingle()
            .Which.Kind.Should().Be(FindingKind.PtrExternal);
    }

    [Fact]
    public void Doppelte_Adresse_ohne_bestaetigenden_PTR_ist_eine_Warnung()
    {
        var alt = A("altes-nas", "172.16.1.25");
        var neu = A("nas-neu", "172.16.1.25");
        var inv = Inventory(alt, neu);

        _analyzer.Analyze(inv, alt, Now).Single(f => f.Kind == FindingKind.DuplicateIp).Severity.Should().Be(Severity.Warning);
        Kinds(inv, neu).Should().Contain(FindingKind.DuplicateIp);
    }

    [Fact]
    public void Zusaetzliche_Namen_fuer_einen_Server_sind_nur_eine_Info()
    {
        // Fall vom ersten echten Lauf (Namen erfunden): zwei statische Namen
        // für einen Server, dessen PTR seinen Computernamen nennt, und der
        // Computername hat selbst einen A-Eintrag auf dieselbe Adresse.
        var main = A("SRV-0815", "172.16.1.183", stamp: Now.AddDays(-1));
        var alias1 = A("APPSERVICE01", "172.16.1.183");
        var alias2 = A("APPPROXY01", "172.16.1.183");
        var inv = Inventory(main, alias1, alias2, Ptr("172.16.1.183", "SRV-0815", stamp: Now.AddDays(-1)));

        foreach (var alias in new[] { alias1, alias2 })
        {
            var findings = _analyzer.Analyze(inv, alias, Now);
            findings.Should().ContainSingle().Which.Should().Match<Finding>(f =>
                f.Kind == FindingKind.AliasName && f.Severity == Severity.Info && f.Text.Contains("SRV-0815.lhp.test"));
        }

        _analyzer.Analyze(inv, main, Now).Should().ContainSingle().Which.Should().Match<Finding>(f =>
            f.Kind == FindingKind.AliasName && f.Text.StartsWith("Weitere Namen", StringComparison.Ordinal));
    }

    [Fact]
    public void Zeigt_der_PTR_auf_einen_Namen_mit_anderer_Adresse_bleibt_es_ein_Widerspruch()
    {
        // Kein Hauptname auf dieser Adresse → kein Alias, sondern Widerspruch.
        var a = A("app01", "172.16.1.184");
        var inv = Inventory(a, A("woanders", "172.16.1.200"), Ptr("172.16.1.184", "woanders"));

        Kinds(inv, a).Should().Contain(FindingKind.PtrMismatch).And.NotContain(FindingKind.AliasName);
    }

    [Fact]
    public void Die_Zonenspitze_eines_DC_ist_keine_doppelte_Adresse()
    {
        // Jeder DC registriert seine Adresse an der Zonenspitze und unter
        // seinem Namen — das ist gewollt und darf nicht als Warnung auftauchen.
        var apex = A(Fwd, "172.16.1.5");
        var dc = A("dc03", "172.16.1.5");
        var gc = A("gc._msdcs." + Fwd, "172.16.1.5");
        var dnsZones = A("DomainDnsZones." + Fwd, "172.16.1.5");
        var inv = Inventory(apex, dc, gc, dnsZones, Ptr("172.16.1.5", "dc03"));

        Kinds(inv, dc).Should().BeEmpty();
        Kinds(inv, apex).Should().NotContain([FindingKind.DuplicateIp, FindingKind.PtrMismatch]);
    }

    [Fact]
    public void CNAME_ins_Leere_ist_ein_Fehler_ausserhalb_der_Zonen_nicht()
    {
        var dangling = Cname("wiki", "wikiserver");
        var external = Cname("portal", "portal.cloud.example");
        var inv = Inventory(dangling, external);

        Kinds(inv, dangling).Should().Equal(FindingKind.CnameDangling);
        Kinds(inv, external).Should().BeEmpty();
    }

    [Fact]
    public void Alter_dynamischer_Eintrag_ist_veraltet_ein_statischer_nie()
    {
        var old = A("pc-alt", "172.16.1.47", stamp: Now.AddDays(-214));
        var fresh = A("pc-neu", "172.16.1.48", stamp: Now.AddDays(-2));
        var manual = A("server", "172.16.1.49");
        var inv = Inventory(old, fresh, manual,
            Ptr("172.16.1.47", "pc-alt"), Ptr("172.16.1.48", "pc-neu"), Ptr("172.16.1.49", "server"));

        var stale = _analyzer.Analyze(inv, old, Now).Single(f => f.Kind == FindingKind.Stale);
        stale.Text.Should().Contain("214 Tagen");
        Kinds(inv, fresh).Should().BeEmpty();
        Kinds(inv, manual).Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeAll_liefert_nur_Eintraege_mit_Befunden()
    {
        var ok = A("ok", "172.16.1.1");
        var missing = A("ohne-ptr", "172.16.1.2");
        var inv = Inventory(ok, Ptr("172.16.1.1", "ok"), missing);

        var all = _analyzer.AnalyzeAll(inv, Now);

        all.Keys.Should().BeEquivalentTo([missing.Key]);
    }

    [Fact]
    public async Task Die_Demodaten_enthalten_jeden_Befund_den_die_Doku_zeigt()
    {
        // Die Bildschirmfotos der README leben davon — fehlt ein Fall, ist das
        // Bild nichtssagend, ohne dass es jemand merkt.
        var server = new DemoDnsServer(Now);
        var snapshot = await server.LoadAsync(null, TestContext.Current.CancellationToken);
        var inv = new DnsInventory(snapshot.Zones, snapshot.Records);

        var kinds = _analyzer.AnalyzeAll(inv, Now).Values.SelectMany(f => f).Select(f => f.Kind).ToHashSet();

        kinds.Should().Contain([
            FindingKind.MissingPtr, FindingKind.PtrOrphan, FindingKind.MissingA, FindingKind.PtrMismatch,
            FindingKind.CnameDangling, FindingKind.DuplicateIp, FindingKind.AliasName, FindingKind.Stale, FindingKind.NoReverseZone,
        ]);
    }
}
