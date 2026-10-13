using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NLog;

namespace DnsPilot.Services;

/// <summary>Ergebnis einer Erreichbarkeitsprüfung.</summary>
/// <param name="Online">Hat die Adresse geantwortet?</param>
/// <param name="Method">Worauf sie geantwortet hat (<c>ICMP</c>, <c>TCP 445</c>), oder null.</param>
/// <param name="RoundTripMs">Antwortzeit, falls gemessen.</param>
public sealed record ProbeResult(bool Online, string? Method, long? RoundTripMs);

/// <summary>Ergebnis einer Namensauflösung.</summary>
/// <param name="Addresses">Gelieferte Adressen; leer = nicht auflösbar.</param>
/// <param name="Error">Fehlertext, falls die Auflösung scheiterte.</param>
public sealed record ResolveResult(IReadOnlyList<string> Addresses, string? Error)
{
    public bool Resolved => Addresses.Count > 0;
}

/// <summary>
/// Die eigentlichen Netzwerkzugriffe einer Prüfung. Als Schnittstelle, damit
/// <see cref="ScanService"/> ohne Netzwerk testbar ist.
/// </summary>
public interface IHostProbe
{
    Task<ProbeResult> ProbeAsync(IPAddress address, ProbeOptions options, CancellationToken token);

    Task<ResolveResult> ResolveAsync(string name, CancellationToken token);

    Task<ResolveResult> ReverseResolveAsync(IPAddress address, CancellationToken token);
}

/// <summary>Stellschrauben einer Prüfung, aus den Einstellungen.</summary>
public sealed record ProbeOptions(int TimeoutMs, bool CheckTcpPorts, IReadOnlyList<int> TcpPorts);

/// <summary>
/// Ping, optional TCP-Verbindungsversuche und DNS-Auflösung über den
/// Resolver dieses Rechners.
/// </summary>
public sealed class HostProbe : IHostProbe
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    public async Task<ProbeResult> ProbeAsync(IPAddress address, ProbeOptions options, CancellationToken token)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(options.TimeoutMs), null, null, token)
                .ConfigureAwait(false);
            if (reply.Status == IPStatus.Success) return new ProbeResult(true, "ICMP", reply.RoundtripTime);
        }
        catch (PingException ex)
        {
            Log.Trace(ex, "Ping auf {0} fehlgeschlagen.", address);
        }

        if (!options.CheckTcpPorts) return new ProbeResult(false, null, null);

        // Viele Server beantworten ICMP nicht (Windows-Firewall-Standard),
        // nehmen aber Verbindungen auf 445 oder 3389 an. Ports nacheinander,
        // nicht parallel — fünf gleichzeitige SYNs auf eine Adresse sind genau
        // das Muster eines Portscans.
        foreach (int port in options.TcpPorts)
        {
            token.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.TimeoutMs);
                await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                return new ProbeResult(true, $"TCP {port}", sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Zeitüberschreitung auf diesem Port — nächster.
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // „Abgelehnt" heißt: da ist ein Host, der antwortet — nur
                // nicht auf diesem Port. Das ist ein Lebenszeichen.
                return new ProbeResult(true, $"TCP {port} (abgelehnt)", sw.ElapsedMilliseconds);
            }
            catch (SocketException ex)
            {
                Log.Trace(ex, "TCP {0}:{1} fehlgeschlagen.", address, port);
            }
        }

        return new ProbeResult(false, null, null);
    }

    public async Task<ResolveResult> ResolveAsync(string name, CancellationToken token)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, token).ConfigureAwait(false);
            return new ResolveResult(addresses.Select(a => a.ToString()).ToList(), null);
        }
        catch (SocketException ex)
        {
            return new ResolveResult([], ex.SocketErrorCode == SocketError.HostNotFound ? "nicht auflösbar" : ex.Message);
        }
    }

    public async Task<ResolveResult> ReverseResolveAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), token).ConfigureAwait(false);
            return new ResolveResult([entry.HostName], null);
        }
        catch (SocketException ex)
        {
            return new ResolveResult([], ex.SocketErrorCode == SocketError.HostNotFound ? "nicht auflösbar" : ex.Message);
        }
    }
}
