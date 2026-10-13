using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLog;

namespace DnsPilot.Views;

/// <summary>
/// System-Tray nach Kroste-Standard:
/// <b>Minimieren</b> → Fenster verschwindet in den Tray (<c>Hide()</c>),
/// <b>Schließen</b> → App beendet regulär (kein <c>ShutdownMode</c>-Umbau nötig),
/// Klick aufs Icon oder Menü „Anzeigen" → Fenster kommt zurück.
///
/// Für ein Nachschlage-Werkzeug ist das die passende Bedienung: man braucht es
/// mehrmals am Tag kurz, und ein Kaltstart samt Laden aller Zonen jedes Mal ist
/// spürbar langsamer als ein Klick aufs Tray-Icon.
/// </summary>
internal sealed class TrayController(Application app, Window window)
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly Application _app = app;
    private readonly Window _window = window;
    private TrayIcon? _tray;
    private bool _restoreInProgress;

    public void Install()
    {
        try
        {
            var iconUri = new Uri("avares://DnsPilot/Assets/dnspilot.png");
            var icon = AssetLoader.Exists(iconUri)
                ? new WindowIcon(new Bitmap(AssetLoader.Open(iconUri)))
                : null;

            _tray = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "DNS-Pilot — DNS-Einträge prüfen und pflegen",
                IsVisible = true,
                Menu = BuildMenu(),
            };
            _tray.Clicked += (_, _) => Restore();

            TrayIcon.SetIcons(_app, new TrayIcons { _tray });
            _window.PropertyChanged += OnWindowPropertyChanged;

            Log.Info("System-Tray installiert (Minimieren → Tray).");
        }
        catch (Exception ex)
        {
            // Ohne verfügbaren Tray verhält sich Minimieren normal und die App
            // bleibt voll nutzbar.
            _tray = null;
            Log.Warn(ex, "System-Tray nicht verfügbar — Rückfall auf Standard-Minimieren.");
        }
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("Anzeigen");
        showItem.Click += (_, _) => Restore();
        menu.Add(showItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Beenden");
        quitItem.Click += (_, _) => Quit();
        menu.Add(quitItem);

        return menu;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        if (_restoreInProgress) return;
        if (_window.WindowState != WindowState.Minimized) return;

        // Hide() schließt nicht — der Prozess bleibt am Leben.
        _window.Hide();
    }

    /// <summary>
    /// Holt das Fenster zurück. Muss public bleiben: der Single-Instance-Guard
    /// ruft es, wenn ein zweiter Start erkannt wurde.
    /// </summary>
    public void Restore()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Ohne dieses Flag feuert das Setzen von WindowState.Normal den
            // Listener oben erneut und es entsteht eine Minimize/Restore-Schleife.
            _restoreInProgress = true;
            try
            {
                _window.Show();
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            }
            finally
            {
                _restoreInProgress = false;
            }
        });
    }

    private void Quit()
    {
        if (_app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
