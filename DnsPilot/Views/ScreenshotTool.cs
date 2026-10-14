#if DEBUG
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DnsPilot.Models;
using DnsPilot.Services;
using DnsPilot.ViewModels;
using NLog;

namespace DnsPilot.Views;

/// <summary>
/// Erzeugt die Bildschirmfotos für die Dokumentation — aufgerufen über
/// <c>DnsPilot.exe --screenshots &lt;Zielordner&gt;</c> (nur im Debug-Build).
///
/// <b>Warum im eigenen Prozess und nicht von außen abgegriffen:</b>
/// <c>PrintWindow</c>, <c>SetForegroundWindow</c> und UI-Automation sehen für
/// verhaltensbasierte Virenscanner wie eine Fernsteuerung aus und werden
/// blockiert. Außerdem wäre das Ergebnis von Fokus, DPI und verdeckenden
/// Fenstern abhängig. Avalonia rendert hier direkt in eine Bitmap — reproduzierbar
/// und ohne jedes Fenster auf dem Bildschirm.
///
/// <b>Die Daten sind frei erfunden</b> (<see cref="DemoDnsServer"/>):
/// Bilder landen in der README, echte Namen und Adressen haben dort nichts zu
/// suchen.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ScreenshotTool
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Zielordner, wenn der Schalter gesetzt wurde — sonst null.</summary>
    public static string? OutputDirectory { get; private set; }

    /// <summary>Liest <c>--screenshots &lt;Ordner&gt;</c> aus den Argumenten.</summary>
    public static bool TryParse(string[] args)
    {
        int i = Array.FindIndex(args, a =>
            a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));

        if (i < 0 || i + 1 >= args.Length) return false;

        OutputDirectory = Path.GetFullPath(args[i + 1]);
        return true;
    }

    /// <summary>
    /// Baut die Fenster mit Demodaten, rendert sie und beendet die Anwendung.
    /// </summary>
    public static async Task RunAsync(Application app, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        // Anmeldeserver und -domäne werden angezeigt (Hinweis im
        // Einstellungen-Fenster). Ohne diese Zeilen stünden echte interne
        // Namen in den Dokumentationsbildern.
        // Wirkt nur in diesem Prozess.
        Environment.SetEnvironmentVariable("USERDNSDOMAIN", "musterstadt.beispiel");
        Environment.SetEnvironmentVariable("USERDOMAIN", "MUSTERSTADT");
        Environment.SetEnvironmentVariable("LOGONSERVER", @"\DC01");

        // Einstellungen in den Temp-Ordner, nicht neben die Bilder — im
        // Zielordner sollen nur die PNGs landen.
        // Einstellungen und Historie in den Temp-Ordner, nicht neben die
        // Bilder — im Zielordner sollen nur die PNGs landen.
        string temp = Path.Combine(Path.GetTempPath(), "DnsPilot-Screenshot");
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);

        var settings = new SettingsService(Path.Combine(temp, "settings.json"));
        settings.Load();
        settings.Current.Server = "DC01";
        settings.Current.UpdateChannel = @"\\dateiserver\software$\DnsPilot";

        // Vorgeschichte, damit im Bild ein Aufräum-Kandidat zu sehen ist: der
        // verwaiste Scanner und der alte PC antworten seit Wochen nicht.
        var sightings = new SightingStore(Path.Combine(temp, "sightings.json"));
        var now = DateTime.UtcNow;
        foreach (string ip in new[] { "10.10.2.55", "10.20.5.47" })
        {
            for (int day = 45; day > 0; day -= 9) sightings.Record(ip, online: false, null, now.AddDays(-day));
        }

        var server = new DemoDnsServer(now);
        var scanner = new ScanService(new DemoHostProbe(server), sightings, settings);
        var backup = new DeletionBackup(Path.Combine(temp, "geloescht"));

        // --- Hauptfenster: alles geprüft, verwaister PTR ausgewählt ----------
        var vm = new MainWindowViewModel(server, settings, new UpdateService(settings), scanner, sightings, backup);
        var main = new MainWindow(vm, new EmptyServices());
        main.Width = 1440;
        main.Height = 860;

        await ShowAndSettleAsync(main);
        await vm.ReloadCommand.ExecuteAsync(null);
        await vm.ScanAsync(vm.VisibleRows);
        vm.SelectedRow = vm.VisibleRows.FirstOrDefault(r => r.Name.StartsWith("10.10.2.55", StringComparison.Ordinal)
            || r.Name.StartsWith("55.2.10.10", StringComparison.Ordinal));
        vm.StatusText = $"{vm.VisibleRows.Count} Einträge geprüft.";
        await SettleAsync(main, rounds: 10);
        Save(main, outputDirectory, "hauptfenster.png");

        // --- Netzsuche --------------------------------------------------------
        vm.SearchText = "10.10.2.0/24";
        vm.ApplyFilter();
        vm.SelectedRow = vm.VisibleRows.FirstOrDefault(r => r.Name.StartsWith("kamera-hof", StringComparison.Ordinal));
        await SettleAsync(main, rounds: 8);
        Save(main, outputDirectory, "netzsuche.png");

        // --- Nur Auffälligkeiten ---------------------------------------------
        vm.SearchText = string.Empty;
        vm.OnlyProblems = true;
        vm.ApplyFilter();
        vm.SelectedRow = vm.VisibleRows.FirstOrDefault(r => r.Name.StartsWith("pc-0388", StringComparison.Ordinal));
        await SettleAsync(main, rounds: 8);
        Save(main, outputDirectory, "auffaelligkeiten.png");

        // --- Befund mit Erklärung und „PTR anlegen" ---------------------------
        vm.OnlyProblems = false;
        vm.AllowChanges = true;
        vm.SearchText = "kamera";
        vm.ApplyFilter();
        vm.SelectedRow = vm.VisibleRows.FirstOrDefault(r => r.Name.StartsWith("kamera-hof", StringComparison.Ordinal));
        await SettleAsync(main, rounds: 8);
        Save(main, outputDirectory, "ptr-fehlt.png");

        // --- Neuer Host --------------------------------------------------------
        var newHost = new NewHostWindowViewModel(server, vm.Inventory, 3600, "10.10.2.33")
        {
            HostLabel = "drucker-og2",
        };
        var newHostWindow = new NewHostWindow(newHost);
        await ShowAndSettleAsync(newHostWindow);
        Save(newHostWindow, outputDirectory, "neuer-host.png");

        // --- Löschen -------------------------------------------------------------
        var toDelete = vm.Inventory.Records.Where(r => r.OwnerName.StartsWith("altes-nas", StringComparison.Ordinal)
            || r.Data.StartsWith("scanner-keller", StringComparison.Ordinal)).ToList();
        var deleteWindow = new DeleteWindow(new DeleteWindowViewModel(toDelete, vm.Inventory));
        await ShowAndSettleAsync(deleteWindow);
        Save(deleteWindow, outputDirectory, "loeschen.png");

        // --- Einstellungen ---------------------------------------------------
        var settingsWindow = new SettingsWindow(new SettingsWindowViewModel(settings, server, new CredentialStore(settings)));
        await ShowAndSettleAsync(settingsWindow);
        Save(settingsWindow, outputDirectory, "einstellungen.png");

        // --- Update-Dialog und „Was ist neu" (Hinweise aus dem echten Changelog) ---
        string changelog = Changelog.LoadEmbedded();
        var newest = Changelog.Sections(changelog)[0].Version;
        var demoUpdate = new UpdateCheckResult(new Version(0, 1, 1), newest, @"\\dateiserver\software$\DnsPilot\demo.zip", null,
            Changelog.Between(changelog, new Version(0, 1, 1), newest));
        var updateDialog = new UpdateDialog(new UpdateService(settings), demoUpdate);
        await ShowAndSettleAsync(updateDialog);
        Save(updateDialog, outputDirectory, "update.png");

        var whatsNew = new WhatsNewWindow("Was ist neu", "Alle Versionshinweise, neueste zuerst.", Changelog.All(changelog));
        await ShowAndSettleAsync(whatsNew);
        Save(whatsNew, outputDirectory, "was-ist-neu.png");

        foreach (var w in new Window[] { main, newHostWindow, deleteWindow, settingsWindow, updateDialog, whatsNew }) w.Close();

        Log.Info("Bildschirmfotos in {0} erzeugt.", outputDirectory);
        if (app.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes
            .IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// Zeigt das Fenster außerhalb des sichtbaren Bereichs und lässt Layout und
    /// Rendering durchlaufen. Ohne einen echten Show()-Durchlauf hat das Fenster
    /// keine Größe und die Bitmap bliebe leer.
    /// </summary>
    private static async Task ShowAndSettleAsync(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-4000, -4000);
        window.ShowInTaskbar = false;
        window.Show();
        await SettleAsync(window, rounds: 8);
    }

    /// <summary>
    /// Gibt dem Dispatcher mehrfach Gelegenheit, ausstehende Layout-, Render-
    /// und Hintergrundarbeit abzuschließen.
    /// </summary>
    private static async Task SettleAsync(Window window, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(120);
        }
    }

    private static void Save(Window window, string directory, string fileName)
    {
        var size = new PixelSize(
            Math.Max(1, (int)window.Bounds.Width),
            Math.Max(1, (int)window.Bounds.Height));

        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);

        string path = Path.Combine(directory, fileName);
        using var stream = File.Create(path);
        // Bitmap.Save(Stream) ohne Optionen ist ab Avalonia 12.1 deprecated und
        // mit TreatWarningsAsErrors ein Compile-Fehler.
        bitmap.Save(stream, new PngBitmapEncoderOptions());

        Log.Info("{0} geschrieben ({1}x{2}).", fileName, size.Width, size.Height);
    }

    /// <summary>
    /// Das Hauptfenster erwartet einen Dienstanbieter für die Nebenfenster. Für
    /// die Bilder werden die nie geöffnet.
    /// </summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
#endif
