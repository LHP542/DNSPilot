using System.Text.Json;
using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.Tests;

/// <summary>
/// Persistenz der Einstellungen. Die beiden wichtigen Zusicherungen: eine
/// defekte Datei wird gesichert statt überschrieben, und ein IO-Fehler wird
/// <b>nicht</b> als Defekt behandelt — sonst räumt das Werkzeug intakte Daten
/// weg, sobald ein Netzlaufwerk kurz weg ist.
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "DnsPilot-Settings-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_folder, "settings.json");

    public SettingsServiceTests() => Directory.CreateDirectory(_folder);

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
    public void Load_startet_mit_Standardwerten_wenn_keine_Datei_da_ist()
    {
        var settings = new SettingsService(Path_).Load();

        settings.Server.Should().Be("DC03");
        settings.MaxParallel.Should().Be(8);
        settings.CheckTcpPorts.Should().BeFalse("Portanfragen sind auffälliger als ein Ping");
        settings.UpdateChannel.Should().Contain(@"\\samba01\");
    }

    [Fact]
    public void Save_und_Load_sind_ein_Rundlauf()
    {
        var service = new SettingsService(Path_);
        service.Load();
        service.Current.Server = "dc01.lhp.intern";
        service.Current.MaxParallel = 4;
        service.Current.AdminUser = @"LHPdm.test";
        service.Save();

        var reloaded = new SettingsService(Path_).Load();

        reloaded.Server.Should().Be("dc01.lhp.intern");
        reloaded.MaxParallel.Should().Be(4);
        reloaded.AdminUser.Should().Be(@"LHPdm.test");
    }

    [Fact]
    public void Save_hinterlaesst_keine_temporaere_Datei()
    {
        var service = new SettingsService(Path_);
        service.Load();
        service.Save();

        File.Exists(Path_ + ".tmp").Should().BeFalse();
        File.Exists(Path_).Should().BeTrue();
    }

    [Fact]
    public void Eine_defekte_Datei_wird_gesichert_statt_ueberschrieben()
    {
        File.WriteAllText(Path_, "{ das ist kein JSON");

        var service = new SettingsService(Path_);
        var settings = service.Load();

        // Es wird mit Standardwerten weitergearbeitet …
        settings.Server.Should().Be("DC03");
        // … aber die kaputte Datei bleibt für die Diagnose erhalten.
        File.Exists(Path_ + ".broken").Should().BeTrue();
        File.ReadAllText(Path_ + ".broken").Should().Contain("kein JSON");
    }

    [Fact]
    public void Eine_leere_JSON_Datei_ergibt_Standardwerte()
    {
        File.WriteAllText(Path_, "null");

        new SettingsService(Path_).Load().Server.Should().Be("DC03");
    }

    [Fact]
    public void Ein_fehlendes_Feld_verhindert_das_Laden_nicht()
    {
        // Eine Datei aus einer älteren Version kennt neuere Felder nicht.
        File.WriteAllText(Path_, """{ "Server": "dc01" }""");

        var settings = new SettingsService(Path_).Load();

        settings.Server.Should().Be("dc01");
        settings.MaxParallel.Should().Be(8);
        settings.RecentSearches.Should().NotBeNull();
    }

    [Fact]
    public void Ein_IO_Fehler_quarantaenisiert_NICHT()
    {
        // Datei gesperrt (Netzlaufwerk kurz weg, Virenscanner): der Inhalt ist
        // in Ordnung, nur gerade nicht lesbar. Ein .broken-Move würde hier gute
        // Daten wegräumen — also genau den Verlust verursachen, den die Regel
        // verhindern soll.
        File.WriteAllText(Path_, JsonSerializer.Serialize(new AppSettings { Server = "wichtig" }));

        using (var _ = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            new SettingsService(Path_).Load().Server.Should().Be("DC03", "Standardwert für diese Sitzung");
        }

        File.Exists(Path_ + ".broken").Should().BeFalse();
        File.ReadAllText(Path_).Should().Contain("wichtig");
    }

    [Fact]
    public void Replace_speichert_und_meldet_die_Aenderung()
    {
        var service = new SettingsService(Path_);
        service.Load();

        bool notified = false;
        service.Changed += (_, _) => notified = true;

        service.Replace(new AppSettings { Server = "neu" });

        notified.Should().BeTrue();
        new SettingsService(Path_).Load().Server.Should().Be("neu");
    }

    [Fact]
    public void DefaultPath_liegt_unter_LocalAppData()
    {
        SettingsService.DefaultPath()
            .Should().StartWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .And.EndWith(Path.Combine("DnsPilot", "settings.json"));
    }
}

/// <summary>Verlaufsliste der Suchbegriffe.</summary>
public class AppSettingsTests
{
    [Fact]
    public void RememberSearch_setzt_den_Begriff_nach_vorn()
    {
        var settings = new AppSettings();

        settings.RememberSearch("a");
        settings.RememberSearch("b");

        settings.RecentSearches.Should().Equal("b", "a");
    }

    [Fact]
    public void RememberSearch_entfernt_Duplikate_unabhaengig_von_der_Schreibweise()
    {
        var settings = new AppSettings();

        settings.RememberSearch("Mueller");
        settings.RememberSearch("schmidt");
        settings.RememberSearch("MUELLER");

        settings.RecentSearches.Should().Equal("MUELLER", "schmidt");
    }

    [Fact]
    public void RememberSearch_kappt_die_Liste()
    {
        var settings = new AppSettings();

        for (int i = 0; i < AppSettings.MaxRecentSearches + 10; i++) settings.RememberSearch($"begriff{i}");

        settings.RecentSearches.Should().HaveCount(AppSettings.MaxRecentSearches);
        settings.RecentSearches[0].Should().Be($"begriff{AppSettings.MaxRecentSearches + 9}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RememberSearch_ignoriert_Leereingaben(string? term)
    {
        var settings = new AppSettings();

        settings.RememberSearch(term);

        settings.RecentSearches.Should().BeEmpty();
    }

    [Fact]
    public void Clone_ist_von_der_Vorlage_entkoppelt()
    {
        // Das Einstellungen-Fenster arbeitet auf einer Kopie, damit
        // "Abbrechen" wirklich verwirft.
        var original = new AppSettings { Server = "alt" };
        original.RememberSearch("x");

        var copy = original.Clone();
        copy.Server = "neu";
        copy.RecentSearches.Add("y");

        original.Server.Should().Be("alt");
        original.RecentSearches.Should().Equal("x");
    }
}
