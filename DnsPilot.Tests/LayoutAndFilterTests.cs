using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;
using DnsPilot.ViewModels;
using DnsPilot.Views;

namespace DnsPilot.Tests;

/// <summary>Regeln zum Wiederherstellen von Fenster und Spalten.</summary>
public class LayoutRulesTests
{
    [Theory]
    [InlineData(null, 100.0, 350.0, 350.0)]     // nie gespeichert → Standard
    [InlineData(500.0, 100.0, 350.0, 500.0)]    // gespeichert → gespeichert
    [InlineData(20.0, 100.0, 350.0, 100.0)]     // zu schmal gespeichert → Minimum
    [InlineData(double.NaN, 100.0, 350.0, 350.0)]
    [InlineData(0.0, 100.0, 350.0, 350.0)]
    public void Restore_haelt_das_Minimum_ein(double? saved, double minimum, double fallback, double expected) =>
        LayoutRules.Restore(saved, minimum, fallback).Should().Be(expected);

    private static readonly (int, int, int, int)[] TwoScreens = [(0, 0, 1920, 1040), (1920, 0, 2560, 1400)];

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(2500, 300, true)]       // zweiter Monitor
    [InlineData(5000, 300, false)]      // rechts neben allen Monitoren
    [InlineData(-3000, 100, false)]     // Monitor links wurde abgezogen
    [InlineData(100, 1400, false)]      // unterhalb
    [InlineData(1800, 100, true)]       // Titelleiste ragt nur teilweise rüber
    public void Fenster_muss_an_der_Titelleiste_greifbar_sein(int x, int y, bool expected) =>
        LayoutRules.IsReachable(x, y, TwoScreens).Should().Be(expected);

    [Theory]
    [InlineData(true, 300.0, false, 0.0, true)]     // proportionale Spalte gezogen → merken
    [InlineData(false, 0.0, false, 0.0, false)]     // proportional geblieben → nicht merken
    [InlineData(true, 96.0, true, 96.0, false)]     // feste Spalte unverändert → nicht merken
    [InlineData(true, 140.0, true, 96.0, true)]     // feste Spalte verbreitert → merken
    public void Nur_selbst_gezogene_Spalten_werden_gemerkt(bool isAbsolute, double width, bool defaultAbsolute, double defaultWidth, bool expected) =>
        LayoutRules.IsUserWidth(isAbsolute, width, defaultAbsolute, defaultWidth).Should().Be(expected);
}

/// <summary>Filter und Layout überleben einen Neustart.</summary>
public class ViewStatePersistenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-View-" + Guid.NewGuid().ToString("N"));

    public ViewStatePersistenceTests() => Directory.CreateDirectory(_folder);

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

    private SettingsService Settings()
    {
        var s = new SettingsService(Path.Combine(_folder, "settings.json"));
        s.Load();
        return s;
    }

    private MainWindowViewModel Vm(SettingsService settings)
    {
        var server = new DemoDnsServer();
        var sightings = new SightingStore(Path.Combine(_folder, "sightings.json"));
        return new MainWindowViewModel(server, settings, new UpdateService(settings),
            new ScanService(new DemoHostProbe(server), sightings, settings), sightings,
            new DeletionBackup(Path.Combine(_folder, "geloescht")));
    }

    [Fact]
    public void Layout_und_Filter_sind_ein_Rundlauf_durch_die_Datei()
    {
        var settings = Settings();
        settings.Current.WindowWidth = 1600;
        settings.Current.WindowX = -1200;
        settings.Current.WindowMaximized = true;
        settings.Current.DetailPaneWidth = 420;
        settings.Current.ColumnWidths["Name"] = 380;
        settings.Current.FilterZone = "musterstadt.beispiel";
        settings.Current.FilterOnlyStatic = true;
        settings.Save();

        var reloaded = Settings().Current;

        reloaded.WindowWidth.Should().Be(1600);
        reloaded.WindowX.Should().Be(-1200);
        reloaded.WindowMaximized.Should().BeTrue();
        reloaded.DetailPaneWidth.Should().Be(420);
        reloaded.ColumnWidths.Should().ContainKey("Name").WhoseValue.Should().Be(380);
        reloaded.FilterZone.Should().Be("musterstadt.beispiel");
        reloaded.FilterOnlyStatic.Should().BeTrue();
    }

    [Fact]
    public void Clone_nimmt_das_Layout_mit()
    {
        // Das Einstellungen-Fenster speichert eine Kopie — fehlte das Layout in
        // Clone(), löschte jedes Speichern dort Fenstergröße und Spalten.
        var original = new AppSettings { WindowWidth = 1500, FilterType = "PTR", DetailPaneWidth = 300 };
        original.ColumnWidths["Daten"] = 200;

        var copy = original.Clone();
        copy.ColumnWidths["Daten"] = 999;

        copy.WindowWidth.Should().Be(1500);
        copy.FilterType.Should().Be("PTR");
        copy.DetailPaneWidth.Should().Be(300);
        original.ColumnWidths["Daten"].Should().Be(200, "die Kopie ist entkoppelt");
    }

    [Fact]
    public async Task Filter_werden_beim_Start_wiederhergestellt_auch_die_Zone_nach_dem_Laden()
    {
        var settings = Settings();
        settings.Current.FilterZone = "10.10.in-addr.arpa";
        settings.Current.FilterType = "PTR";
        settings.Current.FilterOnlyProblems = true;

        var vm = Vm(settings);
        vm.SelectedType.Should().Be("PTR");
        vm.OnlyProblems.Should().BeTrue();

        await vm.ReloadCommand.ExecuteAsync(null);

        vm.SelectedZone.Should().Be("10.10.in-addr.arpa", "die Zone wartet, bis die Zonenliste da ist");
        vm.VisibleRows.Should().NotBeEmpty().And.OnlyContain(r => r.Zone == "10.10.in-addr.arpa" && r.TypeText == "PTR");
        settings.Current.FilterZone.Should().Be("10.10.in-addr.arpa", "der Neuaufbau der Zonenliste darf den Filter nicht löschen");
    }

    [Fact]
    public async Task Eine_verschwundene_Zone_faellt_auf_alle_Zonen_zurueck()
    {
        var settings = Settings();
        settings.Current.FilterZone = "gibt-es-nicht.beispiel";

        var vm = Vm(settings);
        await vm.ReloadCommand.ExecuteAsync(null);

        vm.SelectedZone.Should().Be(MainWindowViewModel.AllZones);
        settings.Current.FilterZone.Should().BeNull();
    }

    [Fact]
    public void Unbekannter_Typ_in_der_Datei_wird_ignoriert()
    {
        var settings = Settings();
        settings.Current.FilterType = "MX";

        Vm(settings).SelectedType.Should().Be(MainWindowViewModel.AllTypes);
    }

    [Fact]
    public async Task Filteraenderungen_landen_in_den_Einstellungen()
    {
        var settings = Settings();
        var vm = Vm(settings);
        await vm.ReloadCommand.ExecuteAsync(null);

        vm.SelectedZone = "musterstadt.beispiel";
        vm.SelectedType = "A";
        vm.OnlyStatic = true;
        vm.SelectedZone = MainWindowViewModel.AllZones;

        settings.Current.FilterZone.Should().BeNull();
        settings.Current.FilterType.Should().Be("A");
        settings.Current.FilterOnlyStatic.Should().BeTrue();
    }
}
