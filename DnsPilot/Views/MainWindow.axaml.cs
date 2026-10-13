using System.Runtime.Versioning;
using System.Text;
using Avalonia;
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
    private readonly SettingsService? _settings;

    /// <summary>Standardbreiten der Tabellenspalten aus dem XAML, vor dem Wiederherstellen.</summary>
    private readonly Dictionary<string, DataGridLength> _defaultColumnWidths = [];

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

        // Im Bildschirmfoto-Modus gibt es keinen Dienstanbieter — dann bleibt
        // das Layout aus dem XAML.
        _settings = services.GetService<SettingsService>();
        if (_settings is not null) RestoreLayout(_settings.Current);

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
            EnsureReachable();
            SearchBox.Focus();
            await viewModel.InitializeAsync().ConfigureAwait(true);
        };

        Closing += (_, _) =>
        {
            if (_settings is not null) CaptureLayout(_settings.Current);
            viewModel.PersistOnExit();
        };
    }

    // ------------------------------------------------------------------
    // Fensterlayout merken
    // ------------------------------------------------------------------

    private void RestoreLayout(Models.AppSettings s)
    {
        Width = LayoutRules.Restore(s.WindowWidth, MinWidth, Width);
        Height = LayoutRules.Restore(s.WindowHeight, MinHeight, Height);

        if (s.WindowX is { } x && s.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }

        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        var detail = ContentGrid.ColumnDefinitions[2];
        detail.Width = new GridLength(LayoutRules.Restore(s.DetailPaneWidth, detail.MinWidth, detail.Width.Value));

        foreach (var column in RecordsGrid.Columns)
        {
            string key = column.Header?.ToString() ?? string.Empty;
            _defaultColumnWidths[key] = column.Width;
            if (s.ColumnWidths.TryGetValue(key, out double saved))
            {
                column.Width = new DataGridLength(LayoutRules.Restore(saved, Math.Max(column.MinWidth, 40), saved));
            }
        }

        Log.Debug("Fensterlayout wiederhergestellt ({0}×{1}, {2} Spalten).", Width, Height, s.ColumnWidths.Count);
    }

    /// <summary>
    /// Holt ein Fenster zurück, das nach dem Abziehen eines Monitors außerhalb
    /// jedes Bildschirms stünde.
    /// </summary>
    private void EnsureReachable()
    {
        if (WindowState != WindowState.Normal || Screens.All.Count == 0) return;

        var areas = Screens.All.Select(sc => (sc.WorkingArea.X, sc.WorkingArea.Y, sc.WorkingArea.Width, sc.WorkingArea.Height));
        if (LayoutRules.IsReachable(Position.X, Position.Y, areas)) return;

        var primary = (Screens.Primary ?? Screens.All[0]).WorkingArea;
        double scale = Screens.Primary?.Scaling ?? 1.0;
        Position = new PixelPoint(
            primary.X + Math.Max(0, (primary.Width - (int)(Width * scale)) / 2),
            primary.Y + Math.Max(0, (primary.Height - (int)(Height * scale)) / 2));
        Log.Info("Gespeicherte Fensterposition liegt auf keinem Bildschirm — Fenster zentriert.");
    }

    private void CaptureLayout(Models.AppSettings s)
    {
        // Größe und Position nur im Normalzustand: maximiert oder im Tray
        // wären es die falschen Maße für den nächsten Start.
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
        }
        if (WindowState != WindowState.Minimized) s.WindowMaximized = WindowState == WindowState.Maximized;

        double detail = ContentGrid.ColumnDefinitions[2].ActualWidth;
        if (detail > 0) s.DetailPaneWidth = detail;

        foreach (var column in RecordsGrid.Columns)
        {
            string key = column.Header?.ToString() ?? string.Empty;
            var standard = _defaultColumnWidths.GetValueOrDefault(key, column.Width);
            bool isAbsolute = column.Width.IsAbsolute;
            double width = isAbsolute ? column.Width.Value : column.ActualWidth;

            if (LayoutRules.IsUserWidth(isAbsolute, width, standard.IsAbsolute, standard.Value)) s.ColumnWidths[key] = width;
            else s.ColumnWidths.Remove(key);
        }
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
            await new AboutWindow(updates, this, check => _viewModel?.SkipUpdate(check)).ShowDialog(this);
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

    public async Task<UpdateDialogResult> ShowUpdateDialogAsync(UpdateCheckResult check)
    {
        var updates = _services?.GetService<UpdateService>();
        if (updates is null) return UpdateDialogResult.Later;

        var dialog = new UpdateDialog(updates, check);
        await dialog.ShowDialog(this);
        return dialog.Result;
    }

    public Task ShowWhatsNewAsync(string headline, string subText, string markdown) =>
        new WhatsNewWindow(headline, subText, markdown).ShowDialog(this);

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
