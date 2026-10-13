using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;
using DnsPilot.ViewModels;

namespace DnsPilot.Tests;

/// <summary>
/// Hauptfenster-Logik gegen den Demo-Server: Laden, Suchen, Filtern, Löschen.
/// Ohne Fenster — das ViewModel kennt nur <see cref="IShellServices"/>.
/// </summary>
public class MainWindowViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-Vm-" + Guid.NewGuid().ToString("N"));
    private readonly DemoDnsServer _server = new();
    private readonly MainWindowViewModel _vm;

    public MainWindowViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        var settings = new SettingsService(Path.Combine(_folder, "settings.json"));
        var sightings = new SightingStore(Path.Combine(_folder, "sightings.json"));
        _vm = new MainWindowViewModel(
            _server,
            settings,
            new UpdateService(settings),
            new ScanService(new DemoHostProbe(_server), sightings, settings),
            sightings,
            new DeletionBackup(Path.Combine(_folder, "geloescht")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Liegengebliebener Temp-Ordner ist kein Testfehler.
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Laden_fuellt_Tabelle_und_Zonenauswahl()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);

        _vm.VisibleRows.Should().HaveCount(_server.Records.Count);
        _vm.ZoneChoices.Should().Contain(["musterstadt.beispiel", "10.10.in-addr.arpa"]);
        _vm.InventorySummary.Should().Contain("Fehler");
        _vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Netzsuche_zeigt_Treffer_Uebersicht_und_freie_Adressen()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);

        _vm.SearchText = "10.10.2.0/24";
        _vm.ApplyFilter();

        _vm.VisibleRows.Should().OnlyContain(r => r.Data.StartsWith("10.10.2.", StringComparison.Ordinal)
            || r.OwnerName.EndsWith(".2.10.10.in-addr.arpa", StringComparison.Ordinal));
        _vm.VisibleRows.Should().Contain(r => r.Name == "10.10.2.55" && r.TypeText == "PTR",
            "auch der verwaiste PTR verweist auf das Netz");
        _vm.NetworkSummary.Should().Contain("10.10.2.0/24").And.Contain("Reverse-Zone 10.10.in-addr.arpa");
        _vm.FreeAddresses.Should().NotContain(a => Ipv4.ToText(a) == "10.10.2.31");
        _vm.CopyFreeAddressesCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Freitext_und_Typfilter_wirken_zusammen()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);

        _vm.SearchText = "drucker";
        _vm.SelectedType = "PTR";
        _vm.ApplyFilter();

        _vm.VisibleRows.Should().ContainSingle().Which.Data.Should().StartWith("drucker-eg");
    }

    [Fact]
    public async Task Nur_Auffaelligkeiten_blendet_saubere_Eintraege_aus()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);

        _vm.OnlyProblems = true;

        _vm.VisibleRows.Should().NotBeEmpty().And.OnlyContain(r => r.NeedsAttention);
        _vm.VisibleRows.Should().NotContain(r => r.Name == "fileserver.musterstadt.beispiel");
    }

    [Fact]
    public async Task Nur_statische_blendet_dynamische_Eintraege_aus()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);
        int all = _vm.VisibleRows.Count;

        _vm.OnlyStatic = true;

        _vm.VisibleRows.Should().NotBeEmpty().And.OnlyContain(r => r.Record.IsStatic);
        _vm.VisibleRows.Should().HaveCountLessThan(all, "die Demodaten enthalten dynamische Clients");
        _vm.VisibleSummary.Should().Contain(" von ");
    }

    [Fact]
    public async Task Auswahl_zeigt_zusammenhaengende_Eintraege()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);

        _vm.SelectedRow = _vm.VisibleRows.Single(r => r.Name == "fileserver.musterstadt.beispiel");

        _vm.RelatedRows.Select(r => r.TypeText).Should().BeEquivalentTo(["PTR", "CNAME"]);
    }

    [Fact]
    public async Task Ohne_Schreibmodus_kein_Anlegen_und_Loeschen()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);
        _vm.SelectedRows = [_vm.VisibleRows[0]];

        _vm.NewHostCommand.CanExecute(null).Should().BeFalse();
        _vm.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();

        _vm.AllowChanges = true;

        _vm.NewHostCommand.CanExecute(null).Should().BeTrue();
        _vm.DeleteSelectedCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Loeschen_sichert_entfernt_und_aktualisiert_die_Tabelle()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);
        var orphan = _vm.VisibleRows.Single(r => r.Data.StartsWith("scanner-keller", StringComparison.Ordinal)).Record;

        await _vm.DeleteAsync([orphan]);

        _vm.VisibleRows.Should().NotContain(r => r.Record.Key == orphan.Key);
        _server.Records.Should().NotContain(r => r.Key == orphan.Key);
        Directory.GetFiles(Path.Combine(_folder, "geloescht")).Should().ContainSingle();
        _vm.StatusText.Should().StartWith("1 von 1");
    }

    [Fact]
    public async Task Ein_bereits_verschwundener_Eintrag_wird_gemeldet_statt_still_uebergangen()
    {
        await _vm.ReloadCommand.ExecuteAsync(null);
        var ghost = new DnsRecord("musterstadt.beispiel", "gibtsnicht.musterstadt.beispiel", RecordType.A, "10.10.9.9", 3600, null);

        await _vm.DeleteAsync([ghost]);

        _vm.ErrorMessage.Should().Contain("gibtsnicht");
        _vm.StatusText.Should().StartWith("0 von 1");
    }
}

/// <summary>Auflösungsvergleich und CSV.</summary>
public class RecordRowTests
{
    private static RecordRow Row(DnsRecord r) => new(r, []);

    [Fact]
    public void A_Aufloesung_auf_die_eigene_Adresse_stimmt()
    {
        var row = Row(TestData.A("drucker", "172.16.1.20"));
        row.ApplyResolve(new ResolveResult(["172.16.1.20"], null));

        row.ResolveProblem.Should().BeFalse();
        row.ResolveText.Should().Be("Auflösung stimmt");
    }

    [Fact]
    public void A_Aufloesung_auf_eine_fremde_Adresse_ist_ein_Problem()
    {
        var row = Row(TestData.A("drucker", "172.16.1.20"));
        row.ApplyResolve(new ResolveResult(["172.16.1.99"], null));

        row.ResolveProblem.Should().BeTrue();
        row.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public void Nicht_aufloesbarer_Name_ist_ein_Problem()
    {
        var row = Row(TestData.A("drucker", "172.16.1.20"));
        row.ApplyResolve(new ResolveResult([], "nicht auflösbar"));

        row.ResolveProblem.Should().BeTrue();
        row.ResolveText.Should().Contain("nicht auflösbar");
    }

    [Fact]
    public void Datensortierung_ordnet_Adressen_numerisch()
    {
        var low = Row(TestData.A("a", "10.0.0.2"));
        var high = Row(TestData.A("b", "10.0.0.100"));

        string.CompareOrdinal(low.DataSortKey, high.DataSortKey).Should().BeNegative();
    }

    [Fact]
    public void CSV_maskiert_Trenner_und_Anfuehrungszeichen()
    {
        RecordCsv.Field("a;b").Should().Be("\"a;b\"");
        RecordCsv.Field("sag \"hallo\"").Should().Be("\"sag \"\"hallo\"\"\"");
        RecordCsv.Field("schlicht").Should().Be("schlicht");

        string csv = RecordCsv.Build([Row(TestData.A("drucker", "172.16.1.20"))]);
        csv.Split("\r\n")[1].Should().StartWith("drucker.lhp.test;A;172.16.1.20;lhp.test;3600;statisch;ungeprüft");
    }

    [Theory]
    [InlineData("445, 3389 22", new[] { 445, 3389, 22 })]
    [InlineData("abc; 0; 70000", new[] { 445, 3389 })]
    [InlineData("80,80", new[] { 80 })]
    public void Portliste_wird_robust_gelesen(string text, int[] expected) =>
        SettingsWindowViewModel.ParsePorts(text).Should().Equal(expected);
}

/// <summary>Das Laden darf an doppelten oder verdeckten Einträgen nicht scheitern.</summary>
public class MainWindowViewModelDuplicateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-Dup-" + Guid.NewGuid().ToString("N"));

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
    public void Gleicher_Name_in_Zone_und_Unterzone_laesst_das_Laden_durch()
    {
        Directory.CreateDirectory(_folder);
        var settings = new SettingsService(Path.Combine(_folder, "settings.json"));
        var sightings = new SightingStore(Path.Combine(_folder, "sightings.json"));
        var server = new DemoDnsServer();
        var vm = new MainWindowViewModel(server, settings, new UpdateService(settings),
            new ScanService(new DemoHostProbe(server), sightings, settings), sightings,
            new DeletionBackup(Path.Combine(_folder, "geloescht")));

        var parent = new DnsZone("20.10.in-addr.arpa", true, ZoneKind.Primary, true);
        var child = new DnsZone("255.20.10.in-addr.arpa", true, ZoneKind.Primary, true);
        DnsRecord Ptr(DnsZone z) => new(z.Name, "10.255.20.10.in-addr.arpa", RecordType.PTR, "app01.dmz.lhp.test", 3600, null);

        vm.SetInventory(new DnsInventory([parent, child], [Ptr(parent), Ptr(child)]));

        vm.VisibleRows.Should().HaveCount(2);
        vm.VisibleRows.Should().ContainSingle(r => r.Findings.Any(f => f.Kind == FindingKind.Shadowed));
    }
}
