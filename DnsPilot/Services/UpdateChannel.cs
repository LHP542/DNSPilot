using System.Text;
using System.Text.RegularExpressions;

namespace DnsPilot.Services;

/// <summary>
/// Erkennung und Auswertung des Update-Kanals.
///
/// DnsPilot bezieht Updates aus einem Ordner im Firmennetz statt von GitHub —
/// die Ausnahme, die der Kroste-Standard für dienstliche Werkzeuge vorsieht:
/// kein Proxy, kein Internetzugang nötig, und das Ausrollen ist ein
/// Kopiervorgang statt eines Release-Workflows. Der Vertrauensanker ist die
/// NTFS-Berechtigung auf dem Ordner.
///
/// Der Kanaltyp ergibt sich aus der <b>Schreibweise</b> des Werts, nicht aus
/// einem zweiten Schalter — ein Pfad und eine Adresse sind nicht zu
/// verwechseln, und eine Einstellung mehr wäre eine mehr, die jemand falsch
/// setzt.
/// </summary>
internal static partial class UpdateChannel
{
    /// <summary>Ist der Wert ein Ordner (UNC, absoluter Pfad, Laufwerksbuchstabe)?</summary>
    public static bool LooksLikeFolder(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return false;

        string s = channel.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "/" deckt den absoluten Unix-Pfad UND "//server/share" ab. Ohne diesen
        // Fall gilt "/srv/rollout" als Adresse, der Checker läuft still gegen
        // GitHub und tut etwas anderes als verlangt — ohne Fehlermeldung.
        return s.StartsWith('\\') || s.StartsWith('/') || (s.Length > 2 && s[1] == ':');
    }

    /// <summary>
    /// Zieht die Version aus einem Paketnamen wie
    /// <c>DnsPilot-1.4.0-win-x64.zip</c>.
    ///
    /// Sie aus dem Paket zu lesen hieße, es bei <b>jedem Programmstart</b>
    /// herunterzuladen und auszupacken — nur um festzustellen, dass sich nichts
    /// geändert hat.
    /// </summary>
    public static Version? ParseVersionFromFileName(string fileName)
    {
        var match = PackagePattern().Match(Path.GetFileName(fileName));
        if (!match.Success) return null;

        return Version.TryParse(match.Groups["version"].Value, out var version) ? version : null;
    }

    /// <summary>Der Dateiname, den ein Paket dieser Version tragen muss.</summary>
    public static string PackageFileName(Version version) =>
        $"DnsPilot-{version.ToString(3)}-win-x64.zip";

    /// <summary>
    /// Sucht im Ordner das Paket mit der <b>höchsten</b> Version.
    ///
    /// Bewusst nicht nach Änderungsdatum: kopiert jemand ein älteres Paket
    /// zurück in den Ordner, ist es die jüngste Datei — nach Zeitstempel
    /// sortiert würde daraus ein „Update" auf eine ältere Version.
    /// </summary>
    public static (Version Version, string Path)? FindNewestPackage(string folder)
    {
        if (!Directory.Exists(folder)) return null;

        (Version Version, string Path)? best = null;
        foreach (string file in Directory.EnumerateFiles(folder, "DnsPilot-*-win-x64.zip"))
        {
            var version = ParseVersionFromFileName(file);
            if (version is null) continue;
            if (best is null || version > best.Value.Version) best = (version, file);
        }
        return best;
    }

    /// <summary>Name der Versionshinweise neben dem Paket, z. B. <c>DnsPilot-1.4.0-notes.md</c>.</summary>
    public static string NotesFileName(Version version) => $"DnsPilot-{version.ToString(3)}-notes.md";

    /// <summary>
    /// Sammelt die Versionshinweise aller Versionen nach der installierten bis
    /// einschließlich der neuen, neueste zuerst — wer zwei Versionen
    /// überspringt, soll auch beide Änderungen sehen. Null, wenn keine
    /// Notes-Datei dabei ist. Muster aus QrCodeBuilder.
    /// </summary>
    public static string? CollectReleaseNotes(string folder, Version installed, Version latest)
    {
        if (!Directory.Exists(folder)) return null;

        var notes = Directory.EnumerateFiles(folder, "DnsPilot-*-notes.md")
            .Select(file => (Version: ParseNotesVersion(Path.GetFileName(file)), File: file))
            .Where(n => n.Version is not null && n.Version > installed && n.Version <= latest)
            .OrderByDescending(n => n.Version)
            .ToList();

        if (notes.Count == 0) return null;

        var text = new StringBuilder();
        foreach (var (_, file) in notes)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append(File.ReadAllText(file).Trim());
        }
        return text.ToString();
    }

    private static Version? ParseNotesVersion(string fileName)
    {
        var match = NotesPattern().Match(fileName);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var v) ? v : null;
    }

    [GeneratedRegex(@"^DnsPilot-(?<version>\d+\.\d+\.\d+)-notes\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex NotesPattern();

    /// <summary>
    /// Normalisiert eine Versionsangabe für den Vergleich: MinVer schreibt
    /// <c>1.4.0+abc1234</c> oder <c>1.4.1-alpha.0.3</c> in die Assembly, und ein
    /// Stringvergleich darauf ist wertlos.
    /// </summary>
    public static Version? ParseAssemblyVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;

        string core = informationalVersion.Split('+')[0].Split('-')[0].Trim();
        return Version.TryParse(core, out var version) ? version : null;
    }

    [GeneratedRegex(@"^DnsPilot-(?<version>\d+\.\d+\.\d+)-win-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex PackagePattern();
}
