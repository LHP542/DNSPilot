using FluentAssertions;
using DnsPilot.Services;

namespace DnsPilot.Tests;

/// <summary>
/// Der Update-Kanal. Zwei Punkte sind hier absichtlich festgenagelt:
/// die Erkennung des Kanaltyps an der Schreibweise, und dass die
/// <b>höchste</b> Version gewinnt und nicht die zuletzt kopierte Datei.
/// </summary>
public class UpdateChannelTests
{
    [Theory]
    [InlineData(@"\\samba01\542$\5424_IT-Basis-Dienste\DnsPilot")]
    [InlineData(@"C:\Rollout\DnsPilot")]
    [InlineData(@"c:\rollout")]
    [InlineData("//server/share/DnsPilot")]
    [InlineData("/srv/rollout")]
    public void LooksLikeFolder_erkennt_Pfade(string channel) =>
        UpdateChannel.LooksLikeFolder(channel).Should().BeTrue();

    [Fact]
    public void LooksLikeFolder_erkennt_auch_absolute_Unix_Pfade()
    {
        // Diese Zeile fehlte in einer früheren Fassung anderswo und hat dazu
        // geführt, dass ein "/srv/rollout" als Adresse galt: der Checker lief
        // still gegen das Netz statt gegen den Ordner — ohne Fehlermeldung.
        UpdateChannel.LooksLikeFolder(Path.GetTempPath()).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://github.com/LHP542/DnsPilot")]
    [InlineData("http://intern/updates")]
    [InlineData("HTTPS://EXAMPLE.COM/x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("DnsPilot")]
    public void LooksLikeFolder_lehnt_Adressen_und_Unsinn_ab(string? channel) =>
        UpdateChannel.LooksLikeFolder(channel).Should().BeFalse();

    [Fact]
    public void ParseVersionFromFileName_liest_die_Version_aus_dem_Namen()
    {
        // Sie aus dem Paket zu lesen hieße, es bei jedem Programmstart
        // herunterzuladen und auszupacken.
        UpdateChannel.ParseVersionFromFileName("DnsPilot-1.4.0-win-x64.zip")
            .Should().Be(new Version(1, 4, 0));
    }

    [Fact]
    public void ParseVersionFromFileName_akzeptiert_einen_vollen_Pfad()
    {
        UpdateChannel.ParseVersionFromFileName(@"\\samba01\542$\x\DnsPilot-2.10.3-win-x64.zip")
            .Should().Be(new Version(2, 10, 3));
    }

    [Theory]
    [InlineData("DnsPilot.zip")]
    [InlineData("DnsPilot-1.4-win-x64.zip")]
    [InlineData("DnsPilot-1.4.0-linux-x64.tar.gz")]
    [InlineData("Anderes-1.4.0-win-x64.zip")]
    [InlineData("DnsPilot-1.4.0-win-x64.zip.bak")]
    public void ParseVersionFromFileName_lehnt_fremde_Namen_ab(string fileName) =>
        UpdateChannel.ParseVersionFromFileName(fileName).Should().BeNull();

    [Fact]
    public void PackageFileName_passt_zum_Parser()
    {
        string name = UpdateChannel.PackageFileName(new Version(3, 2, 1));

        name.Should().Be("DnsPilot-3.2.1-win-x64.zip");
        UpdateChannel.ParseVersionFromFileName(name).Should().Be(new Version(3, 2, 1));
    }

    [Fact]
    public void FindNewestPackage_nimmt_die_hoechste_Version_nicht_die_juengste_Datei()
    {
        using var folder = new TempFolder();

        // Reihenfolge der Erstellung bewusst umgekehrt: das ÄLTERE Paket wird
        // zuletzt geschrieben. Nach Änderungsdatum sortiert wäre das ein
        // "Update" auf eine ältere Version — genau der Fall, der eintritt,
        // wenn jemand ein altes Paket zurück in den Ordner kopiert.
        folder.Write("DnsPilot-1.10.0-win-x64.zip");
        folder.Write("DnsPilot-1.9.0-win-x64.zip");

        var newest = UpdateChannel.FindNewestPackage(folder.Path);

        newest.Should().NotBeNull();
        newest!.Value.Version.Should().Be(new Version(1, 10, 0));
    }

    [Fact]
    public void FindNewestPackage_ignoriert_fremde_Dateien()
    {
        using var folder = new TempFolder();
        folder.Write("liesmich.txt");
        folder.Write("DnsPilot-alt.zip");

        UpdateChannel.FindNewestPackage(folder.Path).Should().BeNull();
    }

    [Fact]
    public void FindNewestPackage_meldet_einen_fehlenden_Ordner_ohne_Ausnahme()
    {
        // Ein Notebook ohne Netzlaufwerk ist der Normalfall, nicht die Störung.
        UpdateChannel.FindNewestPackage(Path.Combine(Path.GetTempPath(), "gibt-es-nicht-" + Guid.NewGuid()))
            .Should().BeNull();
    }

    [Theory]
    [InlineData("1.4.0", 1, 4, 0)]
    [InlineData("1.4.0+abc1234", 1, 4, 0)]
    [InlineData("1.4.1-alpha.0.3", 1, 4, 1)]
    [InlineData("2.0.0-beta.1+deadbee", 2, 0, 0)]
    public void ParseAssemblyVersion_schneidet_MinVer_Metadaten_ab(
        string informational, int major, int minor, int patch)
    {
        // MinVer schreibt Vorabkennung und Commit-Hash mit in die Assembly.
        // Ein Stringvergleich darauf wäre wertlos.
        UpdateChannel.ParseAssemblyVersion(informational)
            .Should().Be(new Version(major, minor, patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("keine Version")]
    public void ParseAssemblyVersion_liefert_null_bei_Unsinn(string? value) =>
        UpdateChannel.ParseAssemblyVersion(value).Should().BeNull();

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DnsPilot-Tests-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Write(string fileName) =>
            File.WriteAllText(System.IO.Path.Combine(Path, fileName), "x");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Ein liegengebliebener Temp-Ordner ist kein Testfehler.
            }
        }
    }
}
