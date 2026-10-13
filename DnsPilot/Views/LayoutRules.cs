namespace DnsPilot.Views;

/// <summary>
/// Regeln zum Wiederherstellen des Fensterlayouts — ohne Avalonia-Typen, damit
/// sie testbar sind.
///
/// <b>Warum Mindestwerte beim Anwenden:</b> Ein gespeichertes Layout überstimmt
/// jede spätere Vorgabe im Code. Wer einmal eine Spalte auf 20 px gezogen hat,
/// sähe sie sonst nie wieder — und eine Korrektur der Standardbreite im Code
/// erreichte nur neue Installationen (Kroste-Falle, in einem anderen Werkzeug
/// bei allen 48 Bestandsnutzern wirkungslos geblieben).
/// </summary>
internal static class LayoutRules
{
    /// <summary>Gespeicherter Wert, aber nie unter dem Minimum; ohne gespeicherten Wert der Standard.</summary>
    public static double Restore(double? saved, double minimum, double fallback) =>
        saved is { } value && double.IsFinite(value) && value > 0 ? Math.Max(minimum, value) : fallback;

    /// <summary>
    /// Ist das Fenster an dieser Stelle auf einem der Bildschirme greifbar?
    /// Verlangt wird, dass ein Stück der Titelleiste (200×40 px ab der linken
    /// oberen Ecke) auf einem Arbeitsbereich liegt — sonst steht das Fenster
    /// nach dem Abziehen eines zweiten Monitors unerreichbar daneben.
    /// </summary>
    public static bool IsReachable(int x, int y, IEnumerable<(int X, int Y, int Width, int Height)> workingAreas)
    {
        const int grabWidth = 200, grabHeight = 40;
        return workingAreas.Any(a =>
            x < a.X + a.Width - 50 && x + grabWidth > a.X + 50
            && y >= a.Y - 10 && y + grabHeight <= a.Y + a.Height + 10);
    }

    /// <summary>
    /// Soll eine Spaltenbreite gespeichert werden? Nur wenn der Nutzer sie
    /// verändert hat: proportionale Spalten werden beim Ziehen absolut, feste
    /// Spalten weichen von ihrem Standard ab. Alles andere bleibt dem Code
    /// überlassen, damit eine geänderte Standardbreite auch ankommt.
    /// </summary>
    public static bool IsUserWidth(bool isAbsolute, double width, bool defaultIsAbsolute, double defaultWidth) =>
        isAbsolute && (!defaultIsAbsolute || Math.Abs(width - defaultWidth) > 1);
}
