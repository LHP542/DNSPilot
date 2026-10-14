using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;
using static DnsPilot.Tests.TestData;

namespace DnsPilot.Tests;

/// <summary>Wann „PTR anlegen" angeboten wird — und wann bewusst nicht.</summary>
public class PtrFixPlannerTests
{
    [Fact]
    public void A_ohne_PTR_bekommt_einen_in_der_zustaendigen_Reverse_Zone()
    {
        var host = A("ansible", "172.16.1.81");

        var fix = PtrFixPlanner.For(Inventory(host), host);

        fix.Should().NotBeNull();
        fix!.ReverseZone.Name.Should().Be(Rev);
    }

    [Fact]
    public void Vorhandener_PTR_auch_auf_anderen_Namen_verhindert_das_Angebot()
    {
        var host = A("neu", "172.16.1.81");

        PtrFixPlanner.For(Inventory(host, Ptr("172.16.1.81", "alt")), host).Should().BeNull();
    }

    [Fact]
    public void Zweiter_Name_auf_derselben_Adresse_macht_es_mehrdeutig()
    {
        var a = A("nas-alt", "172.16.1.25");
        var b = A("nas-neu", "172.16.1.25");

        PtrFixPlanner.Plan(Inventory(a, b), [a, b]).Should().BeEmpty();
    }

    [Fact]
    public void Ohne_Reverse_Zone_oder_in_sekundaerer_Zone_kein_Angebot()
    {
        var gast = A("gast", "192.168.50.5");
        PtrFixPlanner.For(Inventory(gast), gast).Should().BeNull();

        var host = A("x", "172.16.1.9");
        var inv = new DnsInventory([ForwardZone(), new DnsZone(Rev, true, ZoneKind.Secondary, false)], [host]);
        PtrFixPlanner.For(inv, host).Should().BeNull();
    }

    [Fact]
    public void Zonenspitze_und_AD_Dienstnamen_bekommen_keinen_PTR()
    {
        var apex = A(Fwd, "172.16.1.11");
        var dnsZones = A("DomainDnsZones." + Fwd, "172.16.1.12");
        var inv = Inventory(apex, dnsZones);

        PtrFixPlanner.Plan(inv, [apex, dnsZones]).Should().BeEmpty();
    }

    [Fact]
    public void DC_Name_neben_der_Zonenspitze_ist_eindeutig()
    {
        var apex = A(Fwd, "172.16.1.11");
        var dc = A("dc03", "172.16.1.11");

        PtrFixPlanner.Plan(Inventory(apex, dc), [apex, dc]).Should().ContainSingle().Which.Host.Should().Be(dc);
    }

    [Fact]
    public void PTR_und_CNAME_in_der_Auswahl_werden_uebergangen()
    {
        var host = A("web", "172.16.1.40");
        var alias = Cname("intranet", "web");

        PtrFixPlanner.Plan(Inventory(host, alias), [alias, host, host]).Should().ContainSingle();
    }
}

/// <summary>Jeder Befund hat eine Erklärung.</summary>
public class FindingHelpTests
{
    public static TheoryData<FindingKind, Severity> AllKinds()
    {
        var data = new TheoryData<FindingKind, Severity>();
        foreach (var kind in Enum.GetValues<FindingKind>())
        {
            data.Add(kind, Severity.Info);
            data.Add(kind, Severity.Warning);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Jeder_Befund_hat_Titel_Bedeutung_Auswirkung_und_Abhilfe(FindingKind kind, Severity severity)
    {
        var help = FindingHelp.For(kind, severity);

        help.Title.Should().NotBe(kind.ToString());
        help.Meaning.Should().NotBeNullOrWhiteSpace();
        help.Impact.Should().NotBeNullOrWhiteSpace();
        help.Remedy.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Widerspruch_erklaert_Info_und_Warnung_verschieden()
    {
        FindingHelp.For(FindingKind.PtrMismatch, Severity.Info).Impact.Should().Contain("Scavenging");
        FindingHelp.For(FindingKind.PtrMismatch, Severity.Warning).Impact.Should().Contain("statisch");
    }
}
