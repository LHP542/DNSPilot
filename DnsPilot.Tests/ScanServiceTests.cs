using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using DnsPilot.Models;
using DnsPilot.Services;
using static DnsPilot.Tests.TestData;

namespace DnsPilot.Tests;

/// <summary>
/// Drosselung und Entdoppelung der Prüfung. Die Netzwerkzugriffe sind durch
/// eine Attrappe ersetzt, die mitzählt, wie viele gleichzeitig laufen.
/// </summary>
public class ScanServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-Scan-" + Guid.NewGuid().ToString("N"));

    public ScanServiceTests() => Directory.CreateDirectory(_folder);

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

    private sealed class CountingProbe(params string[] online) : IHostProbe
    {
        private int _running;

        public ConcurrentBag<string> Probed { get; } = [];
        public ConcurrentBag<string> Resolved { get; } = [];
        public int MaxConcurrent { get; private set; }

        public async Task<ProbeResult> ProbeAsync(IPAddress address, ProbeOptions options, CancellationToken token)
        {
            int now = Interlocked.Increment(ref _running);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Delay(20, token);
            Interlocked.Decrement(ref _running);

            Probed.Add(address.ToString());
            return online.Contains(address.ToString())
                ? new ProbeResult(true, "ICMP", 1)
                : new ProbeResult(false, null, null);
        }

        public Task<ResolveResult> ResolveAsync(string name, CancellationToken token)
        {
            Resolved.Add(name);
            return Task.FromResult(new ResolveResult(["172.16.1.99"], null));
        }

        public Task<ResolveResult> ReverseResolveAsync(IPAddress address, CancellationToken token) =>
            Task.FromResult(new ResolveResult([], "nicht auflösbar"));
    }

    /// <summary>Sammelt Meldungen synchron — Progress&lt;T&gt; würde sie verzögert zustellen.</summary>
    private sealed class Collect : IProgress<ScanUpdate>
    {
        public ConcurrentBag<ScanUpdate> Updates { get; } = [];
        public void Report(ScanUpdate value) => Updates.Add(value);
    }

    private (ScanService Service, SightingStore Store) Create(IHostProbe probe, int parallel = 3)
    {
        var settings = new SettingsService(Path.Combine(_folder, "settings.json"));
        settings.Current.MaxParallel = parallel;
        settings.Current.PauseBetweenMs = 0;
        var store = new SightingStore(Path.Combine(_folder, "sightings.json"));
        return (new ScanService(probe, store, settings), store);
    }

    [Fact]
    public async Task Nie_mehr_als_MaxParallel_gleichzeitig()
    {
        var probe = new CountingProbe();
        var (service, _) = Create(probe, parallel: 3);
        var targets = Enumerable.Range(1, 30)
            .Select(i => ScanTarget.For(A($"h{i}", $"172.16.1.{i}")) with { ForwardName = null })
            .ToList();

        await service.ScanAsync(targets, new Collect(), TestContext.Current.CancellationToken);

        probe.Probed.Should().HaveCount(30);
        probe.MaxConcurrent.Should().BeLessThanOrEqualTo(3).And.BeGreaterThan(1, "sonst prüft der Test die Drosselung nicht");
    }

    [Fact]
    public async Task Jede_Adresse_wird_pro_Lauf_nur_einmal_angepingt()
    {
        // A und PTR auf dieselbe Adresse, dazu ein zweiter Name: ein Ping reicht.
        var probe = new CountingProbe("172.16.1.25");
        var (service, store) = Create(probe);
        var records = new[] { A("nas", "172.16.1.25"), A("altes-nas", "172.16.1.25"), Ptr("172.16.1.25", "nas") };
        var collect = new Collect();

        await service.ScanAsync(records.Select(ScanTarget.For).ToList(), collect, TestContext.Current.CancellationToken);

        probe.Probed.Should().ContainSingle();
        store.Get("172.16.1.25")!.Checks.Should().Be(1);
        collect.Updates.Should().HaveCount(3).And.OnlyContain(u => u.Probe!.Online);
    }

    [Fact]
    public async Task Ein_CNAME_wird_aufgeloest_und_das_Ergebnis_angepingt()
    {
        var probe = new CountingProbe("172.16.1.99");
        var (service, _) = Create(probe);
        var collect = new Collect();

        await service.ScanAsync([ScanTarget.For(Cname("wiki", "wikiserver"))], collect, TestContext.Current.CancellationToken);

        probe.Resolved.Should().Equal("wiki.lhp.test");
        collect.Updates.Single().ProbedAddress.Should().Be("172.16.1.99");
    }

    [Fact]
    public async Task Ergebnisse_landen_auch_bei_Abbruch_in_der_Historie()
    {
        var probe = new CountingProbe();
        var (service, store) = Create(probe, parallel: 1);
        var targets = Enumerable.Range(1, 50)
            .Select(i => ScanTarget.For(A($"h{i}", $"172.16.1.{i}")) with { ForwardName = null })
            .ToList();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        var act = () => service.ScanAsync(targets, new Collect(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Count.Should().BeGreaterThan(0).And.BeLessThan(50);
        File.Exists(Path.Combine(_folder, "sightings.json")).Should().BeTrue("ein Abbruch speichert trotzdem");
    }

    [Fact]
    public void Ziel_je_Eintragstyp()
    {
        var a = ScanTarget.For(A("drucker", "172.16.1.20"));
        a.Address!.ToString().Should().Be("172.16.1.20");
        a.ForwardName.Should().Be("drucker.lhp.test");

        var ptr = ScanTarget.For(Ptr("172.16.1.20", "drucker"));
        ptr.ReverseAddress!.ToString().Should().Be("172.16.1.20");
        ptr.ForwardName.Should().BeNull();

        var cname = ScanTarget.For(Cname("print", "drucker"));
        cname.Address.Should().BeNull("angepingt wird, was die Auflösung liefert");
        cname.ForwardName.Should().Be("print.lhp.test");
    }
}
