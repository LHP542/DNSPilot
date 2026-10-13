namespace DnsPilot.Views;

/// <summary>
/// Zeigt Versionshinweise aus dem eingebauten Changelog: nach einem Update
/// einmal von selbst, sonst über „Was ist neu" im Über-Fenster.
/// </summary>
public partial class WhatsNewWindow : ChromeWindow
{
    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public WhatsNewWindow()
    {
        InitializeComponent();
    }

    public WhatsNewWindow(string headline, string subText, string markdown) : this()
    {
        Headline.Text = headline;
        SubText.Text = subText;
        ReleaseNotesRenderer.Render(NotesPanel, markdown, "Keine Versionshinweise vorhanden.");
        CloseButton.Click += (_, _) => Close();
    }
}
