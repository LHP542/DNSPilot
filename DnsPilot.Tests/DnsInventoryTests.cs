using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;
using static DnsPilot.Tests.TestData;

namespace DnsPilot.Tests;

/// <summary>Nachschlagen nach Name, Adresse und Netz.</summary>
public class DnsInventoryTests
{
    private static uint Ip(string text)
    {
        Ipv4.TryParseStrict(text, out uint value);
        return value;
    }

    [Fact]
    public void Netzsuche_findet_A_PTR_und_die_CNAMEs_darauf()
    {
        var inv = Inventory(
            A("drucker", "172.16.1.20"),
            Ptr("172.16.1.20", "drucker"),
            Cname("print", "drucker"),
            A("anderswo", "172.16.2.20"));

        var hits = inv.RecordsInRange(SearchQuery.Parse("172.16.1.0/24").Range!.Value);

        hits.Select(r => r.Type).Should().BeEquivalentTo([RecordType.A, RecordType.PTR, RecordType.CNAME]);
        hits.Should().NotContain(r => r.OwnerName.StartsWith("anderswo", StringComparison.Ordinal));
    }

    [Fact]
    public void Ein_verwaister_PTR_wird_ueber_seine_Adresse_gefunden()
    {
        // Genau die Frage „verweist noch etwas auf diese IP?" — auch wenn es
        // keinen A-Eintrag mehr gibt.
        var inv = Inventory(Ptr("172.16.1.99", "weg"));

        inv.RecordsInRange(SearchQuery.Parse("172.16.1.99").Range!.Value).Should().ContainSingle()
            .Which.Type.Should().Be(RecordType.PTR);
    }

    [Fact]
    public void Freie_Adressen_sind_die_ohne_A_und_ohne_PTR()
    {
        var inv = Inventory(
            A("a", "172.16.1.1"),
            Ptr("172.16.1.2", "weg"));

        var free = inv.FreeAddresses(SearchQuery.Parse("172.16.1.0/29").Range!.Value);

        free.Select(Ipv4.ToText).Should().Equal("172.16.1.3", "172.16.1.4", "172.16.1.5", "172.16.1.6");
    }

    [Fact]
    public void Riesige_Bereiche_werden_nicht_durchgerechnet() =>
        Inventory().FreeAddresses(SearchQuery.Parse("10.0.0.0/8").Range!.Value).Should().BeEmpty();

    [Fact]
    public void Die_spezifischste_Reverse_Zone_gewinnt()
    {
        var inv = new DnsInventory(
            [ForwardZone(), ReverseZoneOf("16.172.in-addr.arpa"), ReverseZoneOf("1.16.172.in-addr.arpa")],
            []);

        inv.ReverseZoneFor(Ip("172.16.1.5"))!.Name.Should().Be("1.16.172.in-addr.arpa");
        inv.ReverseZoneFor(Ip("172.16.9.5"))!.Name.Should().Be("16.172.in-addr.arpa");
        inv.ReverseZoneFor(Ip("10.0.0.1")).Should().BeNull();
    }

    [Fact]
    public void Die_laengste_Forward_Zone_gewinnt()
    {
        var inv = new DnsInventory([ForwardZone("lhp.test"), ForwardZone("sub.lhp.test")], []);

        inv.ForwardZoneFor("host.sub.lhp.test")!.Name.Should().Be("sub.lhp.test");
        inv.ForwardZoneFor("host.lhp.test.")!.Name.Should().Be("lhp.test");
        inv.ForwardZoneFor("lhp.test")!.Name.Should().Be("lhp.test");
        inv.ForwardZoneFor("xlhp.test").Should().BeNull("Suffix ohne Punkt davor ist eine andere Zone");
    }

    [Fact]
    public void Related_verbindet_A_mit_PTR_CNAME_und_Namensvettern_auf_derselben_Adresse()
    {
        var a = A("nas", "172.16.1.25");
        var inv = Inventory(
            a,
            A("altes-nas", "172.16.1.25"),
            Ptr("172.16.1.25", "nas"),
            Cname("backup", "nas"));

        inv.Related(a).Select(r => r.OwnerName).Should().BeEquivalentTo(
            ["altes-nas.lhp.test", "25.1.16.172.in-addr.arpa", "backup.lhp.test"]);
    }

    [Fact]
    public void Namen_sind_unabhaengig_von_Schreibweise_und_Schlusspunkt()
    {
        var inv = Inventory(A("Server01", "172.16.1.1"));

        inv.RecordsNamed("server01.LHP.test.").Should().ContainSingle();
    }

    [Fact]
    public void With_fuegt_hinzu_und_entfernt_ohne_das_Original_zu_aendern()
    {
        var keep = A("bleibt", "172.16.1.1");
        var drop = A("geht", "172.16.1.2");
        var inv = Inventory(keep, drop);

        var changed = inv.With(added: [A("neu", "172.16.1.3")], removed: [drop]);

        changed.Records.Select(r => r.HostLabel).Should().BeEquivalentTo(["bleibt", "neu"]);
        inv.Records.Should().HaveCount(2);
        changed.ARecordsAt(Ip("172.16.1.2")).Should().BeEmpty();
    }
}

/// <summary>
/// Derselbe Name in Zone und Unterzone — auf dem Jumphost gefunden
/// (<c>10.255.20.10.in-addr.arpa</c>), und das Laden brach daran ab.
/// </summary>
public class ShadowedRecordTests
{
    private static readonly DnsZone Parent = new("20.10.in-addr.arpa", true, ZoneKind.Primary, true);
    private static readonly DnsZone Child = new("255.20.10.in-addr.arpa", true, ZoneKind.Primary, true);
    private static readonly DnsZone Forward = new("dmz.lhp.test", false, ZoneKind.Primary, true);

    private static DnsRecord PtrIn(DnsZone zone) =>
        new(zone.Name, "10.255.20.10.in-addr.arpa", RecordType.PTR, "app01.dmz.lhp.test", 3600, null);

    [Fact]
    public void Gleicher_PTR_in_Zone_und_Unterzone_sind_zwei_Eintraege()
    {
        PtrIn(Parent).Key.Should().NotBe(PtrIn(Child).Key);

        var inv = new DnsInventory([Forward, Parent, Child], [PtrIn(Parent), PtrIn(Child)]);
        inv.Records.Should().HaveCount(2);
        inv.With(removed: [PtrIn(Parent)]).Records.Should().ContainSingle().Which.Zone.Should().Be(Child.Name);
    }

    [Fact]
    public void Der_Eintrag_in_der_uebergeordneten_Zone_ist_verdeckt()
    {
        var a = new DnsRecord(Forward.Name, "app01.dmz.lhp.test", RecordType.A, "10.20.255.10", 3600, null);
        var inv = new DnsInventory([Forward, Parent, Child], [a, PtrIn(Parent), PtrIn(Child)]);
        var analyzer = new DnsAnalyzer(30);
        var now = DateTime.UtcNow;

        analyzer.Analyze(inv, PtrIn(Parent), now).Select(f => f.Kind).Should().Contain(FindingKind.Shadowed);
        analyzer.Analyze(inv, PtrIn(Child), now).Should().BeEmpty();
        inv.MostSpecificZoneFor("10.255.20.10.in-addr.arpa")!.Name.Should().Be(Child.Name);
    }
}
