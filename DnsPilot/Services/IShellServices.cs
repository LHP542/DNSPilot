using DnsPilot.ViewModels;

namespace DnsPilot.Services;

/// <summary>
/// Die wenigen Fähigkeiten, für die ein ViewModel ein Fenster braucht:
/// Zwischenablage, Dateiauswahl und Dialoge.
///
/// Als Schnittstelle, damit die ViewModels nicht auf <c>TopLevel</c> zugreifen
/// müssen — sonst hängt Logik, die man testen möchte, an einem laufenden
/// Fenster.
/// </summary>
public interface IShellServices
{
    /// <summary>Legt Text in die Zwischenablage.</summary>
    Task CopyToClipboardAsync(string text);

    /// <summary>
    /// Fragt einen Speicherort ab und schreibt <paramref name="content"/>
    /// dorthin. Liefert den gewählten Pfad, oder null bei Abbruch.
    /// </summary>
    Task<string?> SaveTextFileAsync(string suggestedFileName, string content);

    /// <summary>Öffnet eine URL oder einen Pfad mit der Standardanwendung.</summary>
    Task OpenExternalAsync(string target);

    /// <summary>Zeigt den Dialog „Neuer Host" modal.</summary>
    Task ShowNewHostDialogAsync(NewHostWindowViewModel viewModel);

    /// <summary>Zeigt die Löschbestätigung modal.</summary>
    Task ShowDeleteDialogAsync(DeleteWindowViewModel viewModel);

    /// <summary>Zeigt den Update-Dialog mit Versionshinweisen modal.</summary>
    Task<UpdateDialogResult> ShowUpdateDialogAsync(UpdateCheckResult check);

    /// <summary>Zeigt Versionshinweise („Was ist neu") modal.</summary>
    Task ShowWhatsNewAsync(string headline, string subText, string markdown);
}
