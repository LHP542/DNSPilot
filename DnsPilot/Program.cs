using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using DnsPilot.Views;
using NLog;

namespace DnsPilot;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var log = LogManager.GetCurrentClassLogger();

#if DEBUG
        // Werkzeugmodus für die Dokumentationsbilder. Läuft bewusst am
        // Single-Instance-Guard vorbei: er soll eine laufende DNS-Pilot-Instanz
        // nicht stören und selbst keine beanspruchen.
        if (ScreenshotTool.TryParse(args))
        {
            log.Info("Bildschirmfoto-Modus, Zielordner {0}.", ScreenshotTool.OutputDirectory);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
#endif

        // Zweitstart-Prüfung VOR Avalonia: sonst blitzt kurz ein zweites Fenster
        // auf, bevor es sich wieder beendet.
        var guard = new SingleInstanceGuard();
        if (!guard.TryClaim())
        {
            SingleInstanceGuard.NotifyPrimary();
            guard.Dispose();
            return 0;
        }

        App.PendingGuard = guard;
        GlobalExceptionHandler.Install();

        log.Info("DNS-Pilot startet (Benutzer {0}\\{1}, Rechner {2}).",
            Environment.UserDomainName, Environment.UserName, Environment.MachineName);

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "DNS-Pilot konnte nicht gestartet werden.");
            return 1;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    /// <summary>Wird auch vom XAML-Designer benutzt — Signatur nicht ändern.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions
            {
                // WithInterFont() setzt die Standardfamilie über dieselben
                // Options. Da wir sie hier ersetzen, muss Inter erneut angegeben
                // werden — sonst fällt die ganze App auf die System-Schrift
                // zurück.
                DefaultFamilyName = "fonts:Inter#Inter",
                // Ohne Fallback rendern die Piktogramme (🔍 👤 ⚙) als
                // Ersatzkästchen: Inter bringt keine Emoji-Glyphen mit.
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("Segoe UI Emoji") }],
            })
            .LogToTrace();
}
