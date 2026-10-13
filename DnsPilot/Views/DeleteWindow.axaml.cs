using DnsPilot.ViewModels;

namespace DnsPilot.Views;

/// <summary>Löschbestätigung. Schließt sich, wenn das ViewModel es anfordert.</summary>
public partial class DeleteWindow : ChromeWindow
{
    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public DeleteWindow()
    {
        InitializeComponent();
    }

    public DeleteWindow(DeleteWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}
