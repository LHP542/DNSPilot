using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DnsPilot.Services;

namespace DnsPilot.Views;

/// <summary>
/// Baut Versionshinweise als gestaltete Absätze auf — für den Update-Dialog und
/// „Was ist neu". Die Zerlegung macht <see cref="ReleaseNotesFormatter"/> (aus
/// Checkmk Cockpit übernommen); hier steht nur, wie ein Absatz aussieht.
///
/// Farben kommen ausschließlich über Style-Klassen aus <c>App.axaml</c>
/// (<c>note-*</c>), nie als Literal im Code.
/// </summary>
internal static class ReleaseNotesRenderer
{
    public static void Render(Panel target, string? markdown, string emptyText)
    {
        target.Children.Clear();

        var blocks = ReleaseNotesFormatter.Parse(markdown);
        if (blocks.Count == 0)
        {
            var empty = Text(emptyText, "note-text");
            empty.FontStyle = FontStyle.Italic;
            target.Children.Add(empty);
            return;
        }

        foreach (var block in blocks) target.Children.Add(Build(block));
    }

    private static Control Build(NoteBlock block) => block.Kind switch
    {
        NoteBlockKind.Rule => new Avalonia.Controls.Shapes.Rectangle { Classes = { "divider-h" }, Margin = new Thickness(0, 10) },
        NoteBlockKind.Heading => Text(block.Text, "note-heading"),
        // Versionsüberschriften („0.2.0 — Datum") hervorheben: wer mehrere
        // Versionen überspringt, muss sehen, wo eine endet.
        NoteBlockKind.Subheading when char.IsAsciiDigit(block.Text.FirstOrDefault()) => Text(block.Text, "note-heading"),
        NoteBlockKind.Subheading => Text(block.Text, "note-subheading"),

        // Codeblöcke und Tabellen behalten Zeilen und feste Breite und dürfen
        // als einzige waagerecht scrollen.
        NoteBlockKind.Code => new Border
        {
            Classes = { "note-code" },
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock { Text = block.Text, Classes = { "mono", "note-text" }, TextWrapping = TextWrapping.NoWrap },
            },
        },

        NoteBlockKind.Quote => new Border { Classes = { "note-quote" }, Child = Text(block.Text, "note-text") },

        // Der Punkt steht in einer eigenen Spalte, damit Folgezeilen bündig
        // unter dem Text bleiben statt unter den Punkt zu rutschen.
        NoteBlockKind.Bullet => Bullet(block.Text),

        _ => WithMargin(Text(block.Text, "note-text"), new Thickness(0, 3)),
    };

    private static Grid Bullet(string text)
    {
        var dot = new TextBlock { Text = "•", Classes = { "note-dot" }, VerticalAlignment = VerticalAlignment.Top };
        var body = Text(text, "note-text");
        Grid.SetColumn(body, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(4, 2, 0, 2),
            Children = { dot, body },
        };
    }

    /// <summary>Ein Absatz mit echten fetten Stellen statt sichtbarer Sternchen.</summary>
    private static TextBlock Text(string text, string styleClass)
    {
        var block = new TextBlock { Classes = { styleClass }, Inlines = [] };
        foreach (var (part, bold) in ReleaseNotesFormatter.Inline(text))
        {
            // Nur bei fetten Stellen setzen: ein explizites Normal überstimmte
            // sonst die Fettung, die Überschriften aus ihrem Style erben.
            var run = new Run(part);
            if (bold) run.FontWeight = FontWeight.SemiBold;
            block.Inlines.Add(run);
        }
        return block;
    }

    private static T WithMargin<T>(T control, Thickness margin) where T : Control
    {
        control.Margin = margin;
        return control;
    }
}
