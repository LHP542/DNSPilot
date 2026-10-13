using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;
using DnsPilot.ViewModels;
using static DnsPilot.Tests.TestData;

namespace DnsPilot.Tests;

/// <summary>Prüfung neuer Hosts.</summary>
public class NewHostValidatorTests
{
    private static readonly DnsInventory Inv = Inventory(
        A("drucker", "172.16.1.20"),
        Ptr("172.16.1.20", "drucker"));

    [Fact]
    public void Freier_Name_und_freie_Adresse_ohne_Hinweise()
    {
        var check = NewHostValidator.Check(Inv, Fwd, "drucker-og2", "172.16.1.33");

        check.IsValid.Should().BeTrue();
        check.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("-drucker")]
    [InlineData("drucker-")]
    [InlineData("drucker_og")]
    [InlineData("drü cker")]
    public void Ungueltige_Namen_werden_abgelehnt(string label) =>
        NewHostValidator.Check(Inv, Fwd, label, "172.16.1.33").IsValid.Should().BeFalse();

    [Theory]
    [InlineData("172.16.1")]
    [InlineData("172.16.1.256")]
    [InlineData("drucker")]
    public void Ungueltige_Adressen_werden_abgelehnt(string address) =>
        NewHostValidator.Check(Inv, Fwd, "neu", address).IsValid.Should().BeFalse();

    [Fact]
    public void Vergebene_Adresse_und_vorhandener_Name_sind_Hinweise_keine_Fehler()
    {
        var check = NewHostValidator.Check(Inv, Fwd, "drucker", "172.16.1.20");

        check.IsValid.Should().BeTrue();
        check.Warnings.Should().HaveCount(3, "Name existiert, Adresse vergeben, PTR vorhanden");
    }

    [Fact]
    public void Ohne_Reverse_Zone_gibt_es_einen_Hinweis() =>
        NewHostValidator.Check(Inv, Fwd, "gast", "192.168.50.5").Warnings
            .Should().ContainSingle().Which.Should().Contain("keine Reverse-Zone");

    [Fact]
    public void Sekundaere_Zone_ist_nicht_beschreibbar()
    {
        var inv = new DnsInventory([new DnsZone("partner.example", false, ZoneKind.Secondary, false)], []);

        NewHostValidator.Check(inv, "partner.example", "neu", "172.16.1.5").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Mehrteilige_Namen_sind_erlaubt() =>
        NewHostValidator.IsValidHostLabel("drucker.og2").Should().BeTrue();
}

/// <summary>Dialog „Neuer Host" gegen den Demo-Server.</summary>
public class NewHostWindowViewModelTests
{
    [Fact]
    public async Task Anlegen_liefert_A_und_PTR()
    {
        var server = new DemoDnsServer();
        var snapshot = await server.LoadAsync(null, TestContext.Current.CancellationToken);
        var inv = new DnsInventory(snapshot.Zones, snapshot.Records);

        var vm = new NewHostWindowViewModel(server, inv, 3600, "10.10.2.33") { HostLabel = "drucker-og2" };
        vm.CanCreatePtr.Should().BeTrue();
        vm.Zones.Should().NotContain(z => z.StartsWith('_'), "_msdcs ist keine Zone für Hosts");

        await vm.CreateCommand.ExecuteAsync(null);

        vm.Created.Should().NotBeNull();
        vm.Created!.Select(r => r.Type).Should().Equal(RecordType.A, RecordType.PTR);
        vm.Created[1].OwnerName.Should().Be("33.2.10.10.in-addr.arpa");
    }

    [Fact]
    public async Task Hinweise_muessen_bestaetigt_werden()
    {
        var server = new DemoDnsServer();
        var snapshot = await server.LoadAsync(null, TestContext.Current.CancellationToken);
        var inv = new DnsInventory(snapshot.Zones, snapshot.Records);

        // 10.10.1.20 gehört dem Fileserver.
        var vm = new NewHostWindowViewModel(server, inv, 3600, "10.10.1.20") { HostLabel = "zweitname" };

        vm.HasWarnings.Should().BeTrue();
        vm.CreateCommand.CanExecute(null).Should().BeFalse();
        vm.AcceptWarnings = true;
        vm.CreateCommand.CanExecute(null).Should().BeTrue();
    }
}

/// <summary>Löschbestätigung.</summary>
public class DeleteWindowViewModelTests
{
    [Fact]
    public void Der_passende_PTR_wird_mit_vorgeschlagen()
    {
        var a = A("drucker", "172.16.1.20");
        var ptr = Ptr("172.16.1.20", "drucker");
        var fremd = Ptr("172.16.1.20", "anderer");
        var vm = new DeleteWindowViewModel([a], Inventory(a, ptr, fremd));

        vm.Items.Select(i => i.Record).Should().Equal(a, ptr);
        vm.Items[1].Reason.Should().Contain("PTR zu");
    }

    [Fact]
    public void Ohne_Bestaetigung_kein_Loeschen()
    {
        var a = A("drucker", "172.16.1.20");
        var vm = new DeleteWindowViewModel([a], Inventory(a));

        vm.DeleteCommand.CanExecute(null).Should().BeFalse();
        vm.Reviewed = true;
        vm.DeleteCommand.CanExecute(null).Should().BeTrue();

        vm.Items[0].Include = false;
        vm.RecordsToDelete.Should().BeEmpty();
        vm.CountText.Should().StartWith("0 von 1");
    }
}

/// <summary>Sicherung vor dem Löschen.</summary>
public class DeletionBackupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-Backup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Liegengebliebener Temp-Ordner ist kein Testfehler.
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Jeder_Eintrag_landet_als_eigene_Zeile_und_ist_wiederherstellbar()
    {
        var backup = new DeletionBackup(_folder);
        var now = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);

        backup.Write(A("drucker", "172.16.1.20"), "dc03.lhp.test", now);
        string path = backup.Write(Ptr("172.16.1.20", "drucker"), "dc03.lhp.test", now);

        var entries = DeletionBackup.Read(path);
        entries.Should().HaveCount(2);
        entries[0].Should().Match<DeletionBackup.BackupEntry>(e =>
            e.OwnerName == "drucker.lhp.test" && e.Type == "A" && e.Data == "172.16.1.20"
            && e.Ttl == 3600 && e.Zone == Fwd && e.Server == "dc03.lhp.test");
    }
}
