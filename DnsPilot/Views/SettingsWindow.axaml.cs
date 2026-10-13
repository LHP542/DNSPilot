using System.Runtime.Versioning;
using DnsPilot.ViewModels;

namespace DnsPilot.Views;

/// <summary>Einstellungen-Fenster. Schließt sich, wenn das ViewModel es anfordert.</summary>
[SupportedOSPlatform("windows")]
public partial class SettingsWindow : ChromeWindow
{
    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}
