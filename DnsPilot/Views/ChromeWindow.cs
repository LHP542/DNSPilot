using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NLog;

namespace DnsPilot.Views;

/// <summary>
/// Custom-Chrome nach Avalonia-12-Konvention (Kroste-Standard):
/// <c>BorderOnly</c> (NICHT <c>None</c> — sonst fehlen die nativen
/// Resize-Griffe) und Client-Area bis in die Dekoration ausgedehnt. Ohne
/// <c>ExtendClientArea…</c> liegt die OS-Caption-Hit-Test-Zone über der eigenen
/// Titelleiste und schluckt Klicks und Drag.
/// </summary>
public class ChromeWindow : Window
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();
    private static readonly Uri IconUri = new("avares://DnsPilot/Assets/dnspilot.png");

    protected ChromeWindow()
    {
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        CanResize = true;

        try
        {
            if (AssetLoader.Exists(IconUri))
            {
                Icon = new WindowIcon(new Bitmap(AssetLoader.Open(IconUri)));
            }
        }
        catch (Exception ex)
        {
            // Ohne Icon ist die App voll benutzbar — kein Grund, den Start zu
            // verhindern.
            Log.Warn(ex, "Fenster-Icon konnte nicht geladen werden.");
        }
    }
}
