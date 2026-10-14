using DnsPilot.ViewModels;

namespace DnsPilot.Views;

/// <summary>Dialog „Alias umbiegen". Schließt sich, wenn das ViewModel es anfordert.</summary>
public partial class RetargetCnameWindow : ChromeWindow
{
    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public RetargetCnameWindow()
    {
        InitializeComponent();
    }

    public RetargetCnameWindow(RetargetCnameWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Opened += (_, _) => TargetBox.Focus();
    }
}
