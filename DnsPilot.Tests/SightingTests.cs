using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.Tests;

/// <summary>Prüfhistorie und das Urteil daraus.</summary>
public class SightingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-Sightings-" + Guid.NewGuid().ToString("N"));

    public SightingTests() => Directory.CreateDirectory(_folder);

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

    private SightingStore NewStore() => new(Path.Combine(_folder, "sightings.json"));

    [Fact]
    public void Nie_geprueft_ist_ungeprueft() =>
        SightingEvaluator.Evaluate(null, Now, 30, 3).Should().Be(Reachability.Unchecked);

    [Fact]
    public void Letzte_Pruefung_erfolgreich_ist_online()
    {
        var store = NewStore();
        store.Record("10.0.0.1", online: false, null, Now.AddDays(-60));
        var s = store.Record("10.0.0.1", online: true, "ICMP", Now);

        SightingEvaluator.Evaluate(s, Now, 30, 3).Should().Be(Reachability.Online);
        SightingEvaluator.Describe(s, Reachability.Online).Should().Be("online (ICMP)");
    }

    [Fact]
    public void Lange_Zeit_ohne_Antwort_aber_zu_wenige_Pruefungen_ist_kein_Kandidat()
    {
        // Eine einzige Prüfung vor 40 Tagen ist keine Beobachtung über 40 Tage.
        var store = NewStore();
        var s = store.Record("10.0.0.2", online: false, null, Now.AddDays(-40));

        SightingEvaluator.Evaluate(s, Now, 30, 3).Should().Be(Reachability.Offline);
    }

    [Fact]
    public void Viele_Pruefungen_in_kurzer_Zeit_sind_kein_Kandidat()
    {
        var store = NewStore();
        HostSighting? s = null;
        for (int i = 0; i < 10; i++) s = store.Record("10.0.0.3", online: false, null, Now.AddHours(-i));

        SightingEvaluator.Evaluate(s, Now, 30, 3).Should().Be(Reachability.Offline);
    }

    [Fact]
    public void Zeit_und_Fehlversuche_zusammen_ergeben_einen_Kandidaten()
    {
        var store = NewStore();
        HostSighting? s = null;
        foreach (int day in new[] { 45, 30, 10 }) s = store.Record("10.0.0.4", online: false, null, Now.AddDays(-day));

        SightingEvaluator.Evaluate(s, Now, 30, 3).Should().Be(Reachability.Candidate);
        SightingEvaluator.Describe(s, Reachability.Candidate).Should().StartWith("nie erreicht seit");
    }

    [Fact]
    public void Ein_einst_erreichter_Host_wird_ab_der_letzten_Antwort_gezaehlt()
    {
        var store = NewStore();
        store.Record("10.0.0.5", online: true, "ICMP", Now.AddDays(-100));
        store.Record("10.0.0.5", online: false, null, Now.AddDays(-20));
        store.Record("10.0.0.5", online: false, null, Now.AddDays(-10));
        var s = store.Record("10.0.0.5", online: false, null, Now.AddDays(-1));

        s.FailuresSinceOnline.Should().Be(3);
        SightingEvaluator.Evaluate(s, Now, 30, 3).Should().Be(Reachability.Candidate);
        SightingEvaluator.Describe(s, Reachability.Candidate).Should().StartWith("zuletzt online");
    }

    [Fact]
    public void Historie_ueberlebt_Speichern_und_Laden()
    {
        var store = NewStore();
        store.Record("10.0.0.6", online: true, "TCP 445", Now);
        store.Save();

        var reloaded = NewStore();
        reloaded.Load();

        reloaded.Get("10.0.0.6")!.LastMethod.Should().Be("TCP 445");
        reloaded.Count.Should().Be(1);
    }

    [Fact]
    public void Defekte_Historie_wird_gesichert_statt_ueberschrieben()
    {
        string path = Path.Combine(_folder, "sightings.json");
        File.WriteAllText(path, "{ kaputt");

        var store = NewStore();
        store.Load();

        store.Count.Should().Be(0);
        File.Exists(path + ".broken").Should().BeTrue();
    }
}
