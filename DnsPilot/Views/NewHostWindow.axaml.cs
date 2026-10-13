using DnsPilot.ViewModels;

namespace DnsPilot.Views;

/// <summary>Dialog „Neuer Host". Schließt sich, wenn das ViewModel es anfordert.</summary>
public partial class NewHostWindow : ChromeWindow
{
    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public NewHostWindow()
    {
        InitializeComponent();
    }

    public NewHostWindow(NewHostWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Opened += (_, _) => NameBox.Focus();
    }
}
