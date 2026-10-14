using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.Tests;

/// <summary>Auswertung der Skriptausgabe, Zeile für Zeile.</summary>
public class DnsRecordMapperTests
{
    private const char T = '\t';

    [Fact]
    public void Zonenzeile_wird_uebernommen()
    {
        var zone = DnsRecordMapper.ParseZone($"Z{T}1.16.172.in-addr.arpa{T}Primary{T}True{T}True{T}False");

        zone.Should().Be(new DnsZone("1.16.172.in-addr.arpa", true, ZoneKind.Primary, true));
    }

    [Theory]
    [InlineData("TrustAnchors", "Primary", "False")]
    [InlineData("0.in-addr.arpa", "Primary", "True")]
    [InlineData("127.in-addr.arpa", "Primary", "False")]
    [InlineData(".", "Cache", "False")]
    public void Interne_Zonen_werden_ausgeblendet(string name, string type, string autoCreated) =>
        DnsRecordMapper.ParseZone($"Z{T}{name}{T}{type}{T}False{T}True{T}{autoCreated}").Should().BeNull();

    [Fact]
    public void Dynamischer_A_Eintrag_an_der_Zonenspitze()
    {
        var r = DnsRecordMapper.ParseRecord($"R{T}lhp.intern{T}@{T}A{T}600{T}2026-10-13T03:00:00.0000000Z{T}172.16.1.111");

        r.Should().Be(new DnsRecord("lhp.intern", "lhp.intern", RecordType.A, "172.16.1.111", 600,
            new DateTime(2026, 10, 13, 3, 0, 0, DateTimeKind.Utc)));
        r!.TimestampUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Statischer_CNAME_verliert_den_Schlusspunkt()
    {
        var r = DnsRecordMapper.ParseRecord($"R{T}lhp.intern{T}info{T}CNAME{T}3600{T}{T}portal01.lhp.intern.");

        r!.OwnerName.Should().Be("info.lhp.intern");
        r.Data.Should().Be("portal01.lhp.intern");
        r.IsStatic.Should().BeTrue();
    }

    [Fact]
    public void PTR_im_Reverse_Netz_wird_zur_Adresse()
    {
        var r = DnsRecordMapper.ParseRecord($"R{T}1.16.172.in-addr.arpa{T}40{T}PTR{T}1200{T}{T}drucker.lhp.intern.");

        r!.OwnerName.Should().Be("40.1.16.172.in-addr.arpa");
        r.Address!.ToString().Should().Be("172.16.1.40");
        r.Data.Should().Be("drucker.lhp.intern");
    }

    [Fact]
    public void Mehrteilige_Namen_bleiben_relativ_zur_Zone()
    {
        var r = DnsRecordMapper.ParseRecord($"R{T}lhp.intern{T}host.sub{T}A{T}3600{T}{T}10.0.0.1");
        r!.OwnerName.Should().Be("host.sub.lhp.intern");
    }

    [Theory]
    [InlineData($"R\tlhp.intern\t@\tNS\t3600\t\tdc01.lhp.intern.")]
    [InlineData($"R\tlhp.intern\t@\tA\t600")]
    [InlineData($"E\tlhp.intern\tZugriff verweigert")]
    public void Andere_Zeilen_ergeben_keinen_Eintrag(string line) =>
        DnsRecordMapper.ParseRecord(line).Should().BeNull();

    [Theory]
    [InlineData("drucker.lhp.intern", "lhp.intern", "drucker")]
    [InlineData("lhp.intern", "lhp.intern", "@")]
    [InlineData("50.1.16.172.in-addr.arpa", "1.16.172.in-addr.arpa", "50")]
    [InlineData("host.sub.lhp.intern.", "lhp.intern", "host.sub")]
    public void NodeName_ist_die_Gegenrichtung(string owner, string zone, string node)
    {
        DnsRecordMapper.NodeName(owner, zone).Should().Be(node);
        DnsRecordMapper.OwnerFqdn(node, zone).Should().Be(owner.TrimEnd('.'));
    }

    [Fact]
    public void NodeName_lehnt_fremde_Zonen_ab()
    {
        var act = () => DnsRecordMapper.NodeName("host.anders.de", "lhp.intern");
        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// Der DNS-Zugriff über PowerShell, mit einer Attrappe statt powershell.exe:
/// welches Skript entsteht, wie die Ausgabe gelesen wird, wie Fehler ankommen.
/// </summary>
public class PowerShellDnsServerTests : IDisposable
{
    private const char T = '\t';
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-Ps-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;

    public PowerShellDnsServerTests()
    {
        Directory.CreateDirectory(_folder);
        _settings = new SettingsService(Path.Combine(_folder, "settings.json"));
        _settings.Current.Server = "DC01";
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

    private sealed class FakeRunner : IPowerShellRunner
    {
        public List<string> Scripts { get; } = [];
        public Queue<PowerShellResult> Results { get; } = new();

        public Task<PowerShellResult> RunAsync(string script, Action<string>? onLine, CancellationToken token)
        {
            Scripts.Add(script);
            var result = Results.Count > 0 ? Results.Dequeue() : new PowerShellResult(0, ["ZONEN 1"], "");
            foreach (string line in result.Output) onLine?.Invoke(line);
            return Task.FromResult(result);
        }
    }

    private sealed class Collect : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }

    [Fact]
    public async Task Laden_liest_Zonen_und_Eintraege_und_meldet_Fortschritt()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new PowerShellResult(0,
        [
            $"N{T}3",
            $"Z{T}lhp.intern{T}Primary{T}False{T}True{T}False",
            $"Z{T}1.16.172.in-addr.arpa{T}Primary{T}True{T}True{T}False",
            $"Z{T}TrustAnchors{T}Primary{T}False{T}True{T}False",
            $"R{T}lhp.intern{T}@{T}A{T}600{T}2026-10-13T03:00:00.0000000Z{T}172.16.1.111",
            $"R{T}lhp.intern{T}TOOLS01{T}A{T}3600{T}{T}172.16.1.189",
            $"P{T}lhp.intern",
            $"R{T}1.16.172.in-addr.arpa{T}40{T}PTR{T}1200{T}{T}drucker.lhp.intern.",
            $"P{T}1.16.172.in-addr.arpa",
            $"P{T}TrustAnchors",
        ], ""));
        var server = new PowerShellDnsServer(_settings, runner);
        var progress = new Collect();

        var snapshot = await server.LoadAsync(progress, TestContext.Current.CancellationToken);

        snapshot.Zones.Select(z => z.Name).Should().Equal("lhp.intern", "1.16.172.in-addr.arpa");
        snapshot.Records.Should().HaveCount(3);
        progress.Messages.Should().Contain(m => m.StartsWith("Zone 3 von 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Laden_fragt_die_Zonen_nur_einmal_ab()
    {
        // Die vorgeschaltete Verbindungsprüfung kostete auf dem Jumphost 6,8 s
        // pro Start — das Lese-Skript fragt die Zonen ohnehin ab.
        var runner = new FakeRunner();
        runner.Results.Enqueue(new PowerShellResult(0, [$"N{T}0"], ""));
        var server = new PowerShellDnsServer(_settings, runner);

        await server.LoadAsync(null, TestContext.Current.CancellationToken);
        var info = await server.ConnectAsync(TestContext.Current.CancellationToken);

        runner.Scripts.Should().ContainSingle();
        info.ServerName.Should().Be("DC01");
    }

    [Fact]
    public async Task Eine_unlesbare_Zone_haelt_das_Laden_nicht_auf()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new PowerShellResult(0,
        [
            $"N{T}2",
            $"Z{T}lhp.intern{T}Primary{T}False{T}True{T}False",
            $"Z{T}gesperrt.intern{T}Primary{T}False{T}True{T}False",
            $"R{T}lhp.intern{T}a{T}A{T}3600{T}{T}10.0.0.1",
            $"P{T}lhp.intern",
            $"E{T}gesperrt.intern{T}Zugriff verweigert",
            $"P{T}gesperrt.intern",
        ], ""));
        var progress = new Collect();

        var snapshot = await new PowerShellDnsServer(_settings, runner).LoadAsync(progress, TestContext.Current.CancellationToken);

        snapshot.Records.Should().ContainSingle();
        progress.Messages.Should().Contain(m => m.Contains("1 Zonen konnten nicht gelesen werden", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anlegen_setzt_Werte_in_einfache_Anfuehrungszeichen()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new PowerShellResult(0, ["CREATED A", "CREATED PTR"], ""));
        var server = new PowerShellDnsServer(_settings, runner);

        var created = await server.CreateHostAsync(
            new NewHostRequest("lhp.intern", "drucker-og2", System.Net.IPAddress.Parse("172.16.1.50"), "1.16.172.in-addr.arpa", 3600),
            TestContext.Current.CancellationToken);

        string script = runner.Scripts.Single();
        script.Should().Contain("$s = 'DC01'")
            .And.Contain("-ZoneName 'lhp.intern' -Name 'drucker-og2' -IPv4Address '172.16.1.50'")
            .And.Contain("-ZoneName '1.16.172.in-addr.arpa' -Name '50' -PtrDomainName 'drucker-og2.lhp.intern.'")
            .And.Contain("FromSeconds(3600)");
        created.Select(r => r.OwnerName).Should().Equal("drucker-og2.lhp.intern", "50.1.16.172.in-addr.arpa");
    }

    [Fact]
    public async Task Gescheiterter_PTR_nach_angelegtem_A_wird_genau_so_gemeldet()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new PowerShellResult(1, ["CREATED A"], "Failed to create resource record 50"));
        var server = new PowerShellDnsServer(_settings, runner);

        var act = () => server.CreateHostAsync(
            new NewHostRequest("lhp.intern", "x", System.Net.IPAddress.Parse("172.16.1.50"), "1.16.172.in-addr.arpa", 3600),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<DnsAccessException>()).Which.Message.Should().StartWith("Der A-Eintrag wurde angelegt, der PTR nicht");
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, " -AgeRecord")]
    public async Task PTR_ergaenzen_uebernimmt_TTL_und_altert_bei_dynamischem_Host_mit(bool dynamic, string suffix)
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new PowerShellResult(0, ["CREATED PTR"], ""));
        var server = new PowerShellDnsServer(_settings, runner);
        var host = new DnsRecord("lhp.intern", "ansible.lhp.intern", RecordType.A, "172.16.1.81", 1200,
            dynamic ? DateTime.UtcNow.AddDays(-1) : null);

        var ptr = await server.CreatePtrAsync(host, "1.16.172.in-addr.arpa", TestContext.Current.CancellationToken);

        runner.Scripts.Single().Should().Contain(
            "-ZoneName '1.16.172.in-addr.arpa' -Name '81' -PtrDomainName 'ansible.lhp.intern.' -TimeToLive ([TimeSpan]::FromSeconds(1200))"
            + suffix);
        runner.Scripts.Single().Contains("-AgeRecord", StringComparison.Ordinal).Should().Be(dynamic);
        ptr.OwnerName.Should().Be("81.1.16.172.in-addr.arpa");
        ptr.Data.Should().Be("ansible.lhp.intern");
        ptr.IsStatic.Should().Be(!dynamic);
    }

    [Fact]
    public async Task Loeschen_gleicht_den_Inhalt_ab_und_meldet_Verschwundenes()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new PowerShellResult(3, [], "nicht gefunden"));
        var server = new PowerShellDnsServer(_settings, runner);
        var ptr = new DnsRecord("1.16.172.in-addr.arpa", "55.1.16.172.in-addr.arpa", RecordType.PTR, "scanner.lhp.intern", 3600, null);

        var act = () => server.DeleteRecordAsync(ptr, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<DnsAccessException>()).Which.Message.Should().Contain("existiert auf dem Server nicht mehr");
        runner.Scripts.Single().Should()
            .Contain("-ZoneName '1.16.172.in-addr.arpa' -Name '55' -RRType 'PTR'")
            .And.Contain("$_.RecordData.PtrDomainName.TrimEnd('.') -eq 'scanner.lhp.intern'");
    }

    [Theory]
    [InlineData(1, "Zugriff verweigert", "verweigert")]
    [InlineData(1, "Failed to get the zone information for lhp.intern on server DC01. WIN32 5", "verweigert")]
    [InlineData(2, "Modul DnsServer fehlt", "RSAT")]
    [InlineData(1, "Der RPC-Server ist nicht verfügbar.", "nicht erreichbar")]
    [InlineData(1, "Irgendwas anderes", "Der DNS-Server meldet: Irgendwas anderes")]
    public void Fehler_werden_verstaendlich_uebersetzt(int exitCode, string error, string expected) =>
        PowerShellDnsServer.Translate(new PowerShellResult(exitCode, [], error), "DC01").Message.Should().Contain(expected);

    [Fact]
    public async Task Verbindungsfehler_kommt_als_DnsAccessException_an()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new PowerShellResult(1, [], "Zugriff verweigert"));

        var act = () => new PowerShellDnsServer(_settings, runner).ConnectAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DnsAccessException>();
    }

    [Fact]
    public void Kein_Skript_enthaelt_doppelte_Anfuehrungszeichen()
    {
        // Der Runner reicht das Skript als -Command-Argument durch; ein
        // doppeltes Anführungszeichen würde die Kommandozeile zerlegen.
        string[] all =
        [
            PowerShellDnsServer.Scripts.Frame, PowerShellDnsServer.Scripts.Connect, PowerShellDnsServer.Scripts.Load,
            PowerShellDnsServer.Scripts.CreateA, PowerShellDnsServer.Scripts.CreatePtr, PowerShellDnsServer.Scripts.Delete,
            .. Enum.GetValues<RecordType>().Select(PowerShellDnsServer.Scripts.DataExpression),
        ];

        all.Should().OnlyContain(s => !s.Contains('"'));
    }

    [Fact]
    public void Quote_verdoppelt_einfache_Anfuehrungszeichen() =>
        PowerShellRunner.Quote("o'neil; rm -r").Should().Be("'o''neil; rm -r'");
}
