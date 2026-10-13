using System.Text.RegularExpressions;
using FluentAssertions;

namespace DnsPilot.Tests;

/// <summary>
/// Abgleich der XAML-Verweise gegen die Style-Bibliothek in <c>App.axaml</c>.
///
/// <b>Warum das ein Test sein muss und keine Checkliste:</b> Avalonia meldet
/// beide Fehlerklassen hier <b>gar nicht</b> — weder ein
/// <c>Classes="accent"</c>, zu dem es keinen Style gibt, noch ein
/// <c>{DynamicResource GibtsNichtBrush}</c>. Der Build bleibt grün, und das
/// Element rendert einfach falsch: Fluent-Grau statt Akzentfarbe, oder
/// transparent statt Kartenhintergrund. Bei einer Umbenennung von zwei Dutzend
/// Schlüsseln reichen drei übersehene für drei unsichtbare Elemente.
///
/// Ein Test macht daraus einen roten Lauf bei jedem Commit, statt bei jedem,
/// der zufällig an das Prüf-Snippet denkt.
/// </summary>
public partial class XamlConsistencyTests
{
    /// <summary>
    /// Ressourcenschlüssel, die absichtlich nicht im eigenen XAML definiert
    /// sind, weil sie aus dem FluentTheme kommen. Aktuell keine — DnsPilot
    /// benutzt ausschließlich die eigene Palette. Wer hier etwas einträgt,
    /// sollte dazuschreiben warum.
    /// </summary>
    private static readonly HashSet<string> FrameworkKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Style-Klassen, die Avalonia selbst vergibt (Pseudoklassen kommen im
    /// Selektor mit ':' und werden ohnehin nicht erfasst).
    /// </summary>
    private static readonly HashSet<string> FrameworkClasses = new(StringComparer.Ordinal);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DnsPilot.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("die Tests suchen das Repo-Root über die DnsPilot.slnx");
        return dir!.FullName;
    }

    private static IReadOnlyList<(string Path, string Text)> XamlFiles()
    {
        string app = Path.Combine(RepoRoot(), "DnsPilot");

        var files = Directory.GetFiles(app, "*.axaml", SearchOption.AllDirectories)
            .Select(p => (Path: Path.GetRelativePath(app, p), Text: File.ReadAllText(p)))
            .ToList();

        // Greift die Pfadauflösung daneben, findet der Test nichts und wäre
        // stillschweigend grün — schlimmer als gar kein Test.
        files.Should().NotBeEmpty("sonst prüft dieser Test nichts");
        return files;
    }

    [Fact]
    public void Jeder_referenzierte_Ressourcenschluessel_ist_definiert()
    {
        var files = XamlFiles();

        var defined = files
            .SelectMany(f => KeyDefinition().Matches(f.Text).Select(m => m.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var (path, text) in files)
        {
            foreach (Match match in KeyReference().Matches(text))
            {
                string key = match.Groups["key"].Value;
                if (defined.Contains(key) || FrameworkKeys.Contains(key)) continue;
                missing.Add($"{path}: {key}");
            }
        }

        missing.Should().BeEmpty(
            "fehlende DynamicResource-Schlüssel erzeugen KEINEN Compile-Fehler — "
            + "die Eigenschaft bleibt still auf ihrem Standardwert");
    }

    [Fact]
    public void Jede_verwendete_Style_Klasse_hat_einen_Selektor()
    {
        var files = XamlFiles();

        var declared = files
            .SelectMany(f => SelectorAttribute().Matches(f.Text))
            .SelectMany(m => ClassInSelector().Matches(m.Groups["selector"].Value))
            .Select(m => m.Groups["cls"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var (path, text) in files)
        {
            // Classes="a b c"
            foreach (Match match in ClassesAttribute().Matches(text))
            {
                foreach (string cls in match.Groups["value"].Value
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (declared.Contains(cls) || FrameworkClasses.Contains(cls)) continue;
                    missing.Add($"{path}: Classes=\"{cls}\"");
                }
            }

            // Classes.foo="{Binding …}"
            foreach (Match match in ConditionalClassAttribute().Matches(text))
            {
                string cls = match.Groups["cls"].Value;
                if (declared.Contains(cls) || FrameworkClasses.Contains(cls)) continue;
                missing.Add($"{path}: Classes.{cls}");
            }
        }

        missing.Should().BeEmpty(
            "ein toter Classes-Verweis ist gültiges XAML und rendert einfach als Fluent-Standard");
    }

    [Fact]
    public void Farben_stehen_nur_in_der_Palette()
    {
        // Verstreute #RRGGBB-Literale machen einen Palettenwechsel zu einer
        // Suche über das ganze Projekt.
        var offenders = new List<string>();

        foreach (var (path, text) in XamlFiles())
        {
            if (path.Equals("App.axaml", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (Match match in ColourLiteral().Matches(text))
            {
                offenders.Add($"{path}: {match.Value}");
            }
        }

        offenders.Should().BeEmpty("Farben gehören als Ressourcenschlüssel in die Palette in App.axaml");
    }

    [Fact]
    public void Kein_Ressourcenschluessel_ist_doppelt_vergeben()
    {
        // Doppelte x:Key werfen erst beim Laden des Fensters — also beim Nutzer.
        var duplicates = XamlFiles()
            .SelectMany(f => KeyDefinition().Matches(f.Text).Select(m => (f.Path, Key: m.Groups["key"].Value)))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.Path))})")
            .ToList();

        duplicates.Should().BeEmpty();
    }

    [Fact]
    public void Kein_XML_Kommentar_enthaelt_einen_doppelten_Bindestrich()
    {
        // "-- " beendet den Kommentar: der XAML-Parser bricht mit AVLN1001 ab,
        // und die Meldung nennt einen XML-Fehler, keinen Kommentar.
        var offenders = new List<string>();

        foreach (var (path, text) in XamlFiles())
        {
            foreach (Match match in XmlComment().Matches(text))
            {
                if (match.Groups["body"].Value.Contains("--", StringComparison.Ordinal))
                {
                    offenders.Add(path);
                }
            }
        }

        offenders.Should().BeEmpty();
    }

    [GeneratedRegex(@"x:Key=""(?<key>[^""]+)""")]
    private static partial Regex KeyDefinition();

    [GeneratedRegex(@"\{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z0-9_.]+)\s*\}")]
    private static partial Regex KeyReference();

    [GeneratedRegex(@"Selector=""(?<selector>[^""]+)""")]
    private static partial Regex SelectorAttribute();

    [GeneratedRegex(@"\.(?<cls>[A-Za-z][A-Za-z0-9_-]*)")]
    private static partial Regex ClassInSelector();

    [GeneratedRegex(@"\bClasses=""(?<value>[^""{]*)""")]
    private static partial Regex ClassesAttribute();

    [GeneratedRegex(@"\bClasses\.(?<cls>[A-Za-z][A-Za-z0-9_-]*)=""")]
    private static partial Regex ConditionalClassAttribute();

    [GeneratedRegex(@"""#[0-9A-Fa-f]{6,8}""")]
    private static partial Regex ColourLiteral();

    [GeneratedRegex(@"<!--(?<body>.*?)-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();
}
