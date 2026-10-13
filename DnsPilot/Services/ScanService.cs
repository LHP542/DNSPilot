using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DnsPilot.Models;
using NLog;

namespace DnsPilot.Services;

/// <summary>Was zu einem Eintrag geprüft werden soll.</summary>
/// <param name="Key">Schlüssel des Eintrags (<see cref="DnsRecord.Key"/>).</param>
/// <param name="Address">Adresse zum Anpingen, oder null.</param>
/// <param name="ForwardName">Name, der aufgelöst werden soll, oder null.</param>
/// <param name="ReverseAddress">Adresse für eine Rückwärtsauflösung, oder null.</param>
public sealed record ScanTarget(string Key, IPAddress? Address, string? ForwardName, IPAddress? ReverseAddress)
{
    /// <summary>Die passende Prüfung zu einem Eintrag.</summary>
    public static ScanTarget For(DnsRecord record) => record.Type switch
    {
        RecordType.A or RecordType.AAAA => new ScanTarget(record.Key, record.Address, record.OwnerName, null),
        RecordType.PTR => new ScanTarget(record.Key, record.Address, null, record.Address),
        // Bei CNAME wird der Alias aufgelöst; angepingt wird, was dabei herauskommt.
        RecordType.CNAME => new ScanTarget(record.Key, null, record.OwnerName, null),
        _ => new ScanTarget(record.Key, null, null, null),
    };
}

/// <summary>Ergebnis zu einem Eintrag, sobald es vorliegt.</summary>
public sealed record ScanUpdate(
    string Key,
    string? ProbedAddress,
    ProbeResult? Probe,
    ResolveResult? Resolve,
    HostSighting? Sighting,
    int Done,
    int Total);

/// <summary>
/// Prüft Einträge gedrosselt: höchstens <see cref="AppSettings.MaxParallel"/>
/// Netzwerkzugriffe gleichzeitig, mit Pause dazwischen, jede Adresse und jeder
/// Name nur einmal pro Lauf.
///
/// <b>Warum so vorsichtig:</b> Ein Werkzeug, das ein /16 in Sekunden abpingt,
/// sieht für Endpoint-Schutz (Trend Micro) und IDS aus wie ein Angreifer, der
/// ein Netz kartiert. Geprüft werden deshalb nur Adressen, die ohnehin im DNS
/// stehen — es gibt bewusst keine Funktion „ganzes Netz durchscannen".
/// </summary>
public sealed class ScanService(IHostProbe probe, SightingStore sightings, SettingsService settings)
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly IHostProbe _probe = probe;
    private readonly SightingStore _sightings = sightings;
    private readonly SettingsService _settings = settings;

    /// <summary>Uhr für Tests.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public async Task ScanAsync(IReadOnlyList<ScanTarget> targets, IProgress<ScanUpdate> progress, CancellationToken token)
    {
        var cfg = _settings.Current;
        var options = new ProbeOptions(Math.Clamp(cfg.PingTimeoutMs, 100, 10_000), cfg.CheckTcpPorts, cfg.TcpPorts);
        int parallel = Math.Clamp(cfg.MaxParallel, 1, 32);
        int pause = Math.Clamp(cfg.PauseBetweenMs, 0, 5_000);

        using var gate = new SemaphoreSlim(parallel);
        var probes = new ConcurrentDictionary<string, Lazy<Task<ProbeResult>>>(StringComparer.Ordinal);
        var resolves = new ConcurrentDictionary<string, Lazy<Task<ResolveResult>>>(StringComparer.OrdinalIgnoreCase);
        int done = 0;

        Log.Info("Prüfung von {0} Einträgen gestartet (parallel {1}, Pause {2} ms, TCP {3}).",
            targets.Count, parallel, pause, cfg.CheckTcpPorts);
        var started = DateTime.UtcNow;

        async Task<T> Throttled<T>(Func<Task<T>> op)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (pause > 0) await Task.Delay(pause, token).ConfigureAwait(false);
                return await op().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        Task<ResolveResult> Resolve(string key, Func<Task<ResolveResult>> op) =>
            resolves.GetOrAdd(key, _ => new Lazy<Task<ResolveResult>>(() => Throttled(op))).Value;

        Task<ProbeResult> Probe(IPAddress address) =>
            probes.GetOrAdd(address.ToString(), _ => new Lazy<Task<ProbeResult>>(async () =>
            {
                var result = await Throttled(() => _probe.ProbeAsync(address, options, token)).ConfigureAwait(false);
                _sightings.Record(address.ToString(), result.Online, result.Method, UtcNow());
                return result;
            })).Value;

        async Task Run(ScanTarget target)
        {
            ResolveResult? resolve = null;
            if (cfg.CheckResolution)
            {
                if (target.ForwardName is { } name)
                {
                    resolve = await Resolve("F:" + name, () => _probe.ResolveAsync(name, token)).ConfigureAwait(false);
                }
                else if (target.ReverseAddress is { } reverse)
                {
                    resolve = await Resolve("R:" + reverse, () => _probe.ReverseResolveAsync(reverse, token)).ConfigureAwait(false);
                }
            }

            var address = target.Address ?? FirstIpv4(resolve);
            ProbeResult? result = null;
            HostSighting? sighting = null;
            if (address is not null)
            {
                result = await Probe(address).ConfigureAwait(false);
                sighting = _sightings.Get(address.ToString());
            }

            progress.Report(new ScanUpdate(target.Key, address?.ToString(), result, resolve, sighting,
                Interlocked.Increment(ref done), targets.Count));
        }

        try
        {
            await Task.WhenAll(targets.Select(Run)).ConfigureAwait(false);
            Log.Info("Prüfung abgeschlossen: {0} Einträge, {1} Adressen in {2:F1} s.",
                targets.Count, probes.Count, (DateTime.UtcNow - started).TotalSeconds);
        }
        finally
        {
            // Auch ein abgebrochener Lauf hat Ergebnisse — die gehören in die Historie.
            _sightings.Save();
        }
    }

    private static IPAddress? FirstIpv4(ResolveResult? resolve)
    {
        if (resolve is null) return null;
        foreach (string text in resolve.Addresses)
        {
            if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) return ip;
        }
        return null;
    }
}
