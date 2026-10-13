using System.IO.Pipes;
using NLog;

namespace DnsPilot.Views;

/// <summary>
/// Verhindert einen zweiten Programmstart und holt stattdessen das laufende
/// Fenster nach vorn.
///
/// Für ein Werkzeug im Tray ist das Pflicht: zwei Prozesse würden sich sonst um
/// das Tray-Icon und um Schreibzugriffe auf <c>settings.json</c> streiten — und
/// der zweite Start ist fast immer ein versehentlicher Doppelklick, kein
/// Wunsch nach einem zweiten Fenster.
///
/// Umgesetzt über eine Named Pipe: der Konstruktor der Server-Pipe schlägt mit
/// <see cref="IOException"/> fehl, wenn der Name schon vergeben ist. Das ist
/// zuverlässiger als eine Lock-Datei, die ein Absturz liegen lässt.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Der Pipe-Name enthält den Benutzernamen. Ohne das blockieren sich
    /// verschiedene Benutzer auf einem Terminalserver gegenseitig — und
    /// Verwaltungswerkzeuge laufen dort regelmäßig.
    /// </summary>
    private static readonly string PipeName = $"DnsPilot.SingleInstance.{Environment.UserName}";

    private NamedPipeServerStream? _server;
    private CancellationTokenSource? _listener;
    private bool _disposed;

    /// <summary>Wird gefeuert, wenn ein zweiter Start das Fenster anfordert. Läuft im ThreadPool!</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Versucht, die Rolle der einzigen Instanz zu übernehmen.</summary>
    public bool TryClaim()
    {
        try
        {
            _server = CreateServer();
            return true;
        }
        catch (IOException)
        {
            Log.Info("Es läuft bereits eine DNS-Pilot-Instanz.");
            return false;
        }
    }

    private static NamedPipeServerStream CreateServer() => new(
        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>Meldet der laufenden Instanz, dass sie sich zeigen soll.</summary>
    public static void NotifyPrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            client.WriteByte((byte)'A');
            client.Flush();
            Log.Info("Bestehende Instanz wurde benachrichtigt.");
        }
        catch (Exception ex)
        {
            // Die andere Instanz beendet sich womöglich gerade. Kein Grund für
            // eine Fehlermeldung an den Nutzer.
            Log.Warn(ex, "Bestehende Instanz konnte nicht benachrichtigt werden.");
        }
    }

    /// <summary>Startet die Schleife, die auf Aktivierungswünsche wartet.</summary>
    public void StartListening()
    {
        if (_server is null) return;

        _listener = new CancellationTokenSource();
        _ = ListenAsync(_listener.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _server is not null)
        {
            try
            {
                await _server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[1];
                _ = await _server.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                _server.Disconnect();

                Log.Info("Zweiter Start erkannt — Fenster wird in den Vordergrund geholt.");
                ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Fehler beim Warten auf einen zweiten Start — die Schleife wird neu aufgesetzt.");
                try
                {
                    _server.Dispose();
                    _server = CreateServer();
                }
                catch (IOException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _listener?.Cancel();
        _listener?.Dispose();
        _server?.Dispose();
    }
}
