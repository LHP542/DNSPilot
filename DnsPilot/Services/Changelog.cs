using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DnsPilot.Services;

/// <summary>
/// Die Versionshinweise aus <c>CHANGELOG.md</c>, die als Ressource in die EXE
/// eingebaut ist. Damit lassen sich die Hinweise jederzeit lesen — auch ohne
/// Netzlaufwerk und auch für die Version, die schon installiert ist.
///
/// Format (dasselbe, das der Release-Workflow für die Notes-Dateien
/// herausschneidet): je Version ein Abschnitt <c>## X.Y.Z — Datum</c>.
/// </summary>
public static partial class Changelog
{
    /// <summary>Der eingebaute Changelog, oder leer, wenn er fehlt.</summary>
    public static string LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return string.Empty;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Alle Versionsabschnitte, neueste zuerst — ohne den Vorspann über den Abschnitten.</summary>
    public static IReadOnlyList<(Version Version, string Text)> Sections(string markdown)
    {
        var sections = new List<(Version, string)>();
        Version? current = null;
        var text = new StringBuilder();

        void Flush()
        {
            if (current is not null) sections.Add((current, text.ToString().Trim()));
            text.Clear();
        }

        foreach (string line in markdown.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var match = SectionHeading().Match(line);
            if (match.Success)
            {
                Flush();
                current = Version.Parse(match.Groups["version"].Value);
            }

            if (current is not null) text.Append(line).Append('\n');
        }

        Flush();
        return sections.OrderByDescending(s => s.Item1).ToList();
    }

    /// <summary>
    /// Die Abschnitte nach <paramref name="after"/> bis einschließlich
    /// <paramref name="upTo"/>, neueste zuerst — „was ist seit meiner letzten
    /// Version neu".
    /// </summary>
    public static string Between(string markdown, Version after, Version upTo) =>
        string.Join("\n\n", Sections(markdown)
            .Where(s => s.Version > after && s.Version <= upTo)
            .Select(s => s.Text));

    /// <summary>Alle Abschnitte, neueste zuerst.</summary>
    public static string All(string markdown) => string.Join("\n\n", Sections(markdown).Select(s => s.Text));

    [GeneratedRegex(@"^##\s+(?<version>\d+\.\d+\.\d+)\b")]
    private static partial Regex SectionHeading();
}
