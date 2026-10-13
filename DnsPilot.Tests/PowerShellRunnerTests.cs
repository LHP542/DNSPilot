using FluentAssertions;
using DnsPilot.Services;

namespace DnsPilot.Tests;

/// <summary>
/// Gegen das echte powershell.exe: kommt ein mehrzeiliges Skript über
/// <c>-Command</c> unverändert an, mit Exitcode, Fehlerkanal und Umlauten?
/// Genau das lässt sich mit der Attrappe in <see cref="PowerShellDnsServerTests"/>
/// nicht prüfen.
/// </summary>
public class PowerShellRunnerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-Runner-" + Guid.NewGuid().ToString("N"));
    private readonly PowerShellRunner _runner;

    public PowerShellRunnerTests()
    {
        Directory.CreateDirectory(_folder);
        var settings = new SettingsService(Path.Combine(_folder, "settings.json"));
        _runner = new PowerShellRunner(settings, new CredentialStore(settings));
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
    public async Task Mehrzeiliges_Skript_mit_Exitcode_Fehlerkanal_und_Umlauten()
    {
        const string script = """
            $T = [char]9
            function W($x) { [Console]::Out.WriteLine($x) }
            foreach ($i in 1..3) {
              W ('L' + $T + $i + $T + 'Größe' + $T + 'o''neil')
            }
            [Console]::Error.WriteLine('fehler-test')
            exit 3
            """;
        var streamed = new List<string>();

        var result = await _runner.RunAsync(script, l => { lock (streamed) streamed.Add(l); }, TestContext.Current.CancellationToken);

        result.ExitCode.Should().Be(3);
        result.Output.Should().Equal("L\t1\tGröße\to'neil", "L\t2\tGröße\to'neil", "L\t3\tGröße\to'neil");
        result.Error.Should().Contain("fehler-test");
        streamed.Should().HaveCount(3);
    }

    [Fact]
    public async Task Lange_Zeilen_werden_nicht_umgebrochen()
    {
        // Bei umgeleiteter Ausgabe bricht powershell.exe Pipeline-Ausgabe unter
        // Umständen an der Fensterbreite um — [Console]::Out nicht.
        const string script = "[Console]::Out.WriteLine('x' * 500)";

        var result = await _runner.RunAsync(script, null, TestContext.Current.CancellationToken);

        result.Output.Should().ContainSingle().Which.Should().HaveLength(500);
    }

    [Fact]
    public async Task Rahmen_meldet_fehlendes_Modul_oder_laeuft_durch()
    {
        // Lokal ist das DnsServer-Modul meist nicht installiert: dann muss der
        // Rahmen mit Exitcode 2 enden. Ist es installiert, läuft der Testkörper.
        string script = PowerShellDnsServer.Wrap("W 'KOERPER'", "localhost");

        var result = await _runner.RunAsync(script, null, TestContext.Current.CancellationToken);

        if (result.ExitCode == 2)
        {
            result.Error.Should().Contain("Modul DnsServer fehlt");
        }
        else
        {
            result.ExitCode.Should().Be(0);
            result.Output.Should().Contain("KOERPER");
        }
    }

    [Fact]
    public async Task Doppelte_Anfuehrungszeichen_werden_abgewiesen()
    {
        var act = () => _runner.RunAsync("W \"x\"", null, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
