using Avalonia.Threading;
using NLog;

namespace DnsPilot.Views;

/// <summary>
/// Fängt unbehandelte Ausnahmen aus allen drei Quellen ab, die eine
/// Avalonia-App zum stillen Absturz bringen können, und schreibt sie mit
/// vollem Stacktrace ins Log.
///
/// Ohne das verschwindet ein Fehler im Hintergrund-Task spurlos: die Anwendung
/// läuft scheinbar weiter, tut aber nichts mehr — der undankbarste Fehlerfall
/// für den Support.
/// </summary>
internal static class GlobalExceptionHandler
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Wird gefeuert, damit die UI eine Meldung zeigen kann. Läuft auf dem UI-Thread.</summary>
    public static event Action<Exception>? UnhandledException;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception
                     ?? new InvalidOperationException($"Unbekannter Fehler: {e.ExceptionObject}");
            Log.Fatal(ex, "Unbehandelte Ausnahme (AppDomain), Beenden={0}", e.IsTerminating);
            LogManager.Flush();
            Report(ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unbeobachtete Ausnahme in einem Task.");
            // Als beobachtet markieren: sonst reißt der Finalizer je nach
            // Konfiguration den Prozess mit.
            e.SetObserved();
            Report(e.Exception);
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unbehandelte Ausnahme im UI-Thread.");
            // Verhindert, dass ein Fehler in einem Handler die ganze Oberfläche
            // mitreißt — die Meldung geht stattdessen an den Nutzer.
            e.Handled = true;
            Report(e.Exception);
        };

        Log.Debug("GlobalExceptionHandler installiert.");
    }

    private static void Report(Exception ex)
    {
        var handler = UnhandledException;
        if (handler is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                handler(ex);
            }
            catch (Exception nested)
            {
                Log.Error(nested, "Fehlermeldung konnte nicht angezeigt werden.");
            }
        });
    }
}
