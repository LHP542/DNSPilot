using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;
using DnsPilot.ViewModels;
using static DnsPilot.Tests.TestData;

namespace DnsPilot.Tests;

/// <summary>Prüfung eines neuen Alias-Ziels.</summary>
public class CnameTargetValidatorTests
{
    private static readonly DnsRecord Wiki = Cname("wiki", "wikiserver");

    private static readonly DnsInventory Inv = Inventory(
        Wiki,
        A("fileserver", "172.16.1.20"),
        Cname("intranet", "fileserver"),
        Cname("kreis-a", "wiki"));

    [Fact]
    public void Kurzname_bekommt_die_Zone_des_Alias_und_zeigt_was_dahinter_steht()
    {
        var check = CnameTargetValidator.Check(Inv, Wiki, "fileserver");

        check.IsValid.Should().BeTrue();
        check.Fqdn.Should().Be($"fileserver.{Fwd}");
        check.Warnings.Should().BeEmpty();
        check.Info.Should().Contain("A 172.16.1.20");
    }

    [Theory]
    [InlineData("")]
    [InlineData("file server")]
    [InlineData("wiki")]
    [InlineData("wikiserver")]
    public void Leer_ungueltig_sich_selbst_oder_unveraendert_wird_abgelehnt(string input) =>
        CnameTargetValidator.Check(Inv, Wiki, input).IsValid.Should().BeFalse();

    [Fact]
    public void Nicht_vorhandenes_Ziel_in_eigener_Zone_muss_bestaetigt_werden()
    {
        var check = CnameTargetValidator.Check(Inv, Wiki, "gibtsnicht");

        check.IsValid.Should().BeTrue();
        check.Warnings.Should().ContainSingle().Which.Should().Contain("ins Leere");
    }

    [Fact]
    public void Externes_Ziel_ist_nur_ein_Hinweis()
    {
        var check = CnameTargetValidator.Check(Inv, Wiki, "dienst.cloud.example");

        check.Warnings.Should().BeEmpty();
        check.Info.Should().Contain("außerhalb");
    }

    [Fact]
    public void Alias_als_Ziel_warnt_vor_der_Kette()
    {
        var check = CnameTargetValidator.Check(Inv, Wiki, "intranet");

        check.IsValid.Should().BeTrue();
        check.Warnings.Should().ContainSingle().Which.Should().Contain("selbst ein Alias");
    }

    [Fact]
    public void Ziel_das_ueber_Aliase_zurueckfuehrt_ist_eine_Schleife() =>
        CnameTargetValidator.Check(Inv, Wiki, "kreis-a").Errors.Should().ContainSingle().Which.Should().Contain("Schleife");
}

/// <summary>Der Dialog gegen den Demo-Server: sichern, umbiegen, schließen.</summary>
public class RetargetCnameWindowViewModelTests
{
    [Fact]
    public async Task Umbiegen_sichert_zuerst_und_liefert_den_geaenderten_Eintrag()
    {
        var server = new DemoDnsServer();
        var snapshot = await server.LoadAsync(null, TestContext.Current.CancellationToken);
        var inv = new DnsInventory(snapshot.Zones, snapshot.Records);
        var wiki = snapshot.Records.Single(r => r.OwnerName.StartsWith("wiki.", StringComparison.Ordinal));
        var backedUp = new List<DnsRecord>();
        var vm = new RetargetCnameWindowViewModel(server, inv, wiki, r => { backedUp.Add(r); return "sicherung.jsonl"; });
        bool closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.RetargetCommand.CanExecute(null).Should().BeFalse("noch kein Ziel eingegeben");
        vm.Suggestions.Should().Contain("fileserver.musterstadt.beispiel").And.NotContain(wiki.OwnerName);

        vm.Target = "fileserver";
        await vm.RetargetCommand.ExecuteAsync(null);

        backedUp.Should().Equal(wiki);
        vm.Updated!.Data.Should().Be("fileserver.musterstadt.beispiel");
        server.Records.Should().Contain(vm.Updated).And.NotContain(wiki);
        closed.Should().BeTrue();
    }

    [Fact]
    public async Task Ohne_Sicherung_wird_nicht_umgebogen()
    {
        var server = new DemoDnsServer();
        var snapshot = await server.LoadAsync(null, TestContext.Current.CancellationToken);
        var wiki = snapshot.Records.Single(r => r.OwnerName.StartsWith("wiki.", StringComparison.Ordinal));
        var vm = new RetargetCnameWindowViewModel(server, new DnsInventory(snapshot.Zones, snapshot.Records), wiki,
            _ => throw new IOException("Platte voll"))
        {
            Target = "fileserver",
        };

        await vm.RetargetCommand.ExecuteAsync(null);

        vm.Updated.Should().BeNull();
        vm.ResultMessage.Should().Contain("nicht sichern");
        server.Records.Should().Contain(wiki);
    }
}
