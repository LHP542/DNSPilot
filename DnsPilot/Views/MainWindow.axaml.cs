using System.Runtime.Versioning;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DnsPilot.Services;
using DnsPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace DnsPilot.Views;

/// <summary>
/// Hauptfenster. Stellt dem ViewModel die fensterabhängigen Fähigkeiten bereit
/// (Zwischenablage, Dateiauswahl, Dialoge) und reicht die Mehrfachauswahl der
/// Tabelle durch — <c>DataGrid.SelectedItems</c> ist nicht bindbar.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class MainWindow : ChromeWindow, IShellServices
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly MainWindowViewModel? _viewModel;
    private readonly IServiceProvider? _services;

    /// <summary>Parameterloser Konstruktor für den XAML-Designer.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel, IServiceProvider services) : this()
    {
        _viewModel = viewModel;
        _services = services;

        DataContext = viewModel;
        viewModel.AttachShell(this);

        SettingsButton.Click += (_, _) => OpenSettings();
        AboutButton.Click += (_, _) => OpenAbout();

        RecordsGrid.SelectionChanged += (_, _) =>
            viewModel.SelectedRows = RecordsGrid.SelectedItems.OfType<RecordRow>().ToList();

        // Enter im Suchfeld übernimmt den Begriff in den Verlauf.
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) viewModel.CommitSearchCommand.Execute(null);
        };

        KeyDown += OnKeyDown;

        Opened += async (_, _) =>
        {
            SearchBox.Focus();
            await viewModel.InitializeAsync().ConfigureAwait(true);
        };

        Closing += (_, _) => viewModel.PersistOnExit();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && _viewModel.ReloadCommand.CanExecute(null))
        {
            _viewModel.ReloadCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && RecordsGrid.IsKeyboardFocusWithin
                 && _viewModel.DeleteSelectedCommand.CanExecute(null))
        {
            _viewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.SearchText = string.Empty;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private async void OpenSettings()
    {
        if (_services is null || _viewModel is null) return;

        try
        {
            var vm = _services.GetRequiredService<SettingsWindowViewModel>();
            await new SettingsWindow(vm).ShowDialog(this);

            if (vm.Saved) await _viewModel.OnSettingsChangedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Einstellungen-Fenster konnte nicht geöffnet werden.");
        }
    }

    private async void OpenAbout()
    {
        if (_services is null) return;

        try
        {
            var updates = _services.GetRequiredService<UpdateService>();
            await new AboutWindow(updates, this).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Über-Fenster konnte nicht geöffnet werden.");
        }
    }

    // ------------------------------------------------------------------
    // IShellServices
    // ------------------------------------------------------------------

    public Task ShowNewHostDialogAsync(NewHostWindowViewModel viewModel) =>
        new NewHostWindow(viewModel).ShowDialog(this);

    public Task ShowDeleteDialogAsync(DeleteWindowViewModel viewModel) =>
        new DeleteWindow(viewModel).ShowDialog(this);

    public async Task CopyToClipboardAsync(string text)
    {
        try
        {
            // Avalonia 12: Clipboard läuft über IAsyncDataTransfer; SetTextAsync
            // ist eine Erweiterungsmethode aus Avalonia.Input.Platform.
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Text konnte nicht in die Zwischenablage gelegt werden.");
        }
    }

    public async Task<string?> SaveTextFileAsync(string suggestedFileName, string content)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Einträge exportieren",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "csv",
                FileTypeChoices =
                [
                    new FilePickerFileType("CSV-Datei") { Patterns = ["*.csv"] },
                ],
            });

            if (file is null) return null;

            await using var stream = await file.OpenWriteAsync();
            // UTF-8 MIT BOM: ohne das öffnet Excel die Datei als ANSI und macht
            // aus jedem Umlaut zwei Zeichen.
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await writer.WriteAsync(content);

            return file.Path.LocalPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export nach {0} fehlgeschlagen.", suggestedFileName);
            return null;
        }
    }

    public async Task OpenExternalAsync(string target)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(target));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "{0} konnte nicht geöffnet werden.", target);
        }
    }
}
