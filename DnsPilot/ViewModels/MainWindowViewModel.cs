using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsPilot.Models;
using DnsPilot.Services;
using NLog;

namespace DnsPilot.ViewModels;

/// <summary>
/// Zustand des Hauptfensters: Laden, Suchen und Filtern. Prüfen steht in
/// <c>MainWindowViewModel.Checks.cs</c>, Anlegen und Löschen in
/// <c>MainWindowViewModel.Changes.cs</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    public const string AllZones = "Alle Zonen";
    public const string AllTypes = "Alle Typen";

    private readonly IDnsServer _server;
    private readonly SettingsService _settings;
    private readonly UpdateService _updates;

    private IShellServices? _shell;
    private DnsInventory _inventory = DnsInventory.Empty;
    private DnsServerInfo? _serverInfo;
    private List<RecordRow> _allRows = [];
    private Dictionary<string, RecordRow> _rowsByKey = new(StringComparer.Ordinal);
    private CancellationTokenSource? _filterDebounce;

    /// <summary>Gemerkter Zonenfilter, bis die Zonenliste geladen ist.</summary>
    private string? _pendingZone;

    /// <summary>
    /// Während die Zonenliste neu aufgebaut wird: Die ComboBox setzt ihre
    /// Auswahl beim <c>Clear()</c> kurz auf null — das darf den gemerkten
    /// Filter nicht überschreiben.
    /// </summary>
    private bool _rebuildingZones;

    public MainWindowViewModel(
        IDnsServer server,
        SettingsService settings,
        UpdateService updates,
        ScanService scanner,
        SightingStore sightings,
        DeletionBackup backup)
    {
        _server = server;
        _settings = settings;
        _updates = updates;
        _scanner = scanner;
        _sightings = sightings;
        _backup = backup;

        RecentSearches = new ObservableCollection<string>(settings.Current.RecentSearches);

        // Zuletzt benutzte Filter. Die Zone kann erst gesetzt werden, wenn die
        // Zonenliste geladen ist — bis dahin wartet sie in _pendingZone.
        var c = settings.Current;
        _pendingZone = c.FilterZone;
        _selectedType = c.FilterType is { } t && TypeChoices.Contains(t) ? t : AllTypes;
        _onlyProblems = c.FilterOnlyProblems;
        _onlyStatic = c.FilterOnlyStatic;
    }

    /// <summary>Wird vom Fenster nach dem Laden gesetzt.</summary>
    internal void AttachShell(IShellServices shell) => _shell = shell;

    /// <summary>Aktuelles Inventar (für Dialoge und Tests).</summary>
    internal DnsInventory Inventory => _inventory;

    // ------------------------------------------------------------------
    // Zustand
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanVisibleCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateMissingPtrCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetargetCnameCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Bereit.";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _connectionText = "Nicht verbunden";

    [ObservableProperty]
    private string _inventorySummary = "Noch nichts geladen.";

    /// <summary>Zuletzt gesuchte Begriffe für das Verlaufs-Dropdown.</summary>
    public ObservableCollection<string> RecentSearches { get; }

    public ObservableCollection<string> ZoneChoices { get; } = [AllZones];

    public IReadOnlyList<string> TypeChoices { get; } = [AllTypes, "A", "AAAA", "CNAME", "PTR"];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedZone = AllZones;

    [ObservableProperty]
    private string _selectedType = AllTypes;

    [ObservableProperty]
    private bool _onlyProblems;

    /// <summary>
    /// Nur statische Einträge. Dynamische räumt der DNS-Server selbst weg
    /// (Scavenging auf einem der DCs); die statischen fasst niemand
    /// an — dort liegt der Altbestand, den man von Hand prüfen muss.
    /// </summary>
    [ObservableProperty]
    private bool _onlyStatic;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanVisibleCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    private IReadOnlyList<RecordRow> _visibleRows = [];

    [ObservableProperty]
    private string _visibleSummary = string.Empty;

    /// <summary>Netzübersicht, wenn nach einer Adresse oder einem Netz gesucht wird.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNetworkSummary))]
    private string? _networkSummary;

    public bool HasNetworkSummary => !string.IsNullOrEmpty(NetworkSummary);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyFreeAddressesCommand))]
    private IReadOnlyList<uint> _freeAddresses = [];

    partial void OnSearchTextChanged(string value) => ScheduleFilter();

    partial void OnSelectedZoneChanged(string value)
    {
        // Die ComboBox schreibt null zurück, wenn ihre Liste geleert wird.
        // Während des Neuaufbaus nichts tun — die Auswahl wird danach gesetzt.
        // Hier sofort „Alle Zonen" zu setzen, war der erste Anlauf: die Liste
        // ist in dem Moment leer, und die spätere Zuweisung desselben Werts
        // löste keine Änderung mehr aus — die ComboBox blieb leer.
        if (value is null)
        {
            if (!_rebuildingZones) SelectedZone = AllZones;
            return;
        }

        if (!_rebuildingZones) _settings.Current.FilterZone = value == AllZones ? null : value;
        ApplyFilter();
    }

    partial void OnSelectedTypeChanged(string value)
    {
        if (value is null)
        {
            SelectedType = AllTypes;
            return;
        }

        _settings.Current.FilterType = value == AllTypes ? null : value;
        ApplyFilter();
    }

    partial void OnOnlyProblemsChanged(bool value)
    {
        _settings.Current.FilterOnlyProblems = value;
        ApplyFilter();
    }

    partial void OnOnlyStaticChanged(bool value)
    {
        _settings.Current.FilterOnlyStatic = value;
        ApplyFilter();
    }

    // ------------------------------------------------------------------
    // Laden
    // ------------------------------------------------------------------

    private bool CanReload() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusText = "Zonen werden geladen …";

        try
        {
            var progress = new Progress<string>(text => StatusText = text);
            var snapshot = await _server.LoadAsync(progress, CancellationToken.None).ConfigureAwait(true);

            // Nach dem Laden steht die Verbindung fest — das liefert die
            // zwischengespeicherte Info, ohne den Server noch einmal zu fragen.
            var info = await _server.ConnectAsync(CancellationToken.None).ConfigureAwait(true);
            _serverInfo = info;
            ConnectionText = info.StatusText;

            SetInventory(new DnsInventory(snapshot.Zones, snapshot.Records));
            StatusText = $"{snapshot.Records.Count:N0} Einträge aus {snapshot.Zones.Count} Zonen in {snapshot.Duration.TotalSeconds:F1} s geladen.";
        }
        catch (DnsAccessException ex)
        {
            ConnectionText = "Nicht verbunden";
            Fail(ex.Message, ex);
        }
        catch (Exception ex)
        {
            ConnectionText = "Nicht verbunden";
            Fail("Laden fehlgeschlagen. Details stehen im Log.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Übernimmt ein neues Inventar: Analyse, Zeilen, Zonenauswahl, Filter.</summary>
    internal void SetInventory(DnsInventory inventory)
    {
        _inventory = inventory;
        var now = DateTime.UtcNow;
        var cfg = _settings.Current;

        var findings = new DnsAnalyzer(cfg.StaleAfterDays).AnalyzeAll(inventory, now);
        var previous = _rowsByKey;

        _allRows = inventory.Records
            .Select(r =>
            {
                var row = new RecordRow(r, findings.TryGetValue(r.Key, out var f) ? f : []);
                string? ip = r.Address?.ToString();
                row.ApplySighting(ip is null ? null : _sightings.Get(ip), now, cfg.CandidateAfterDays, cfg.CandidateMinChecks);

                // Auflösungsergebnis einer früheren Prüfung nicht verlieren,
                // nur weil nach einer Änderung neu aufgebaut wird.
                if (previous.TryGetValue(r.Key, out var old) && old.ResolveText is not null)
                {
                    row.ResolveText = old.ResolveText;
                    row.ResolveProblem = old.ResolveProblem;
                }
                return row;
            })
            .OrderBy(r => r.NameSortKey, StringComparer.Ordinal)
            .ThenBy(r => r.Record.Type)
            .ToList();
        // TryAdd statt ToDictionary: ein doppelter Schlüssel darf nie das
        // Laden abbrechen — real passiert, solange der Schlüssel die Zone nicht
        // enthielt (derselbe PTR in Zone und Unterzone, 2026-10-13).
        _rowsByKey = new Dictionary<string, RecordRow>(_allRows.Count, StringComparer.Ordinal);
        foreach (var row in _allRows)
        {
            if (!_rowsByKey.TryAdd(row.Record.Key, row)) Log.Warn("Doppelter Eintrag übergangen: {0}", row.Record.Key);
        }

        string zone = _pendingZone ?? SelectedZone;
        _pendingZone = null;
        _rebuildingZones = true;
        try
        {
            ZoneChoices.Clear();
            ZoneChoices.Add(AllZones);
            foreach (var z in inventory.Zones.OrderBy(z => z.IsReverse).ThenBy(z => z.Name, StringComparer.OrdinalIgnoreCase))
            {
                ZoneChoices.Add(z.Name);
            }
            SelectedZone = ZoneChoices.Contains(zone) ? zone : AllZones;
        }
        finally
        {
            _rebuildingZones = false;
        }

        // Auch wenn sich der Wert nicht geändert hat: die ComboBox hat beim
        // Leeren ihre Auswahl verloren und muss sie neu gemeldet bekommen.
        OnPropertyChanged(nameof(SelectedZone));
        _settings.Current.FilterZone = SelectedZone == AllZones ? null : SelectedZone;

        int errors = _allRows.Count(r => r.HasError);
        int warnings = _allRows.Count(r => r.HasWarning);
        InventorySummary = $"{inventory.Zones.Count} Zonen · {_allRows.Count:N0} Einträge · {errors} Fehler · {warnings} Warnungen";
        RefreshPtrFixes();

        ApplyFilter();
    }

    // ------------------------------------------------------------------
    // Suche und Filter
    // ------------------------------------------------------------------

    /// <summary>Kurz warten, damit nicht bei jedem Tastendruck gefiltert wird.</summary>
    private async void ScheduleFilter()
    {
        _filterDebounce?.Cancel();
        _filterDebounce = new CancellationTokenSource();
        var token = _filterDebounce.Token;

        try
        {
            await Task.Delay(250, token).ConfigureAwait(true);
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // Nächster Tastendruck kam dazwischen.
        }
    }

    /// <summary>Baut die sichtbare Liste aus Suche und Filtern neu auf.</summary>
    internal void ApplyFilter()
    {
        var query = SearchQuery.Parse(SearchText);
        IEnumerable<RecordRow> rows = _allRows;

        if (query.Kind == SearchKind.Range && query.Range is { } range)
        {
            var keys = _inventory.RecordsInRange(range).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            rows = rows.Where(r => keys.Contains(r.Record.Key));
            UpdateNetworkSummary(range, keys.Count);
        }
        else
        {
            NetworkSummary = null;
            FreeAddresses = [];
            if (query.Kind == SearchKind.Text)
            {
                string needle = query.Text.ToLowerInvariant();
                rows = rows.Where(r => r.SearchBlob.Contains(needle, StringComparison.Ordinal));
            }
        }

        if (SelectedZone != AllZones)
        {
            rows = rows.Where(r => r.Zone.Equals(SelectedZone, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedType != AllTypes)
        {
            rows = rows.Where(r => r.TypeText == SelectedType);
        }

        if (OnlyProblems) rows = rows.Where(r => r.NeedsAttention);
        if (OnlyStatic) rows = rows.Where(r => r.Record.IsStatic);

        var selectedKey = SelectedRow?.Record.Key;
        VisibleRows = rows.ToList();
        SelectedRow = selectedKey is null ? null : VisibleRows.FirstOrDefault(r => r.Record.Key == selectedKey);

        VisibleSummary = VisibleRows.Count == _allRows.Count
            ? $"{VisibleRows.Count:N0} Einträge"
            : $"{VisibleRows.Count:N0} von {_allRows.Count:N0} Einträgen";
    }

    private void UpdateNetworkSummary(IpRange range, int recordCount)
    {
        var reverse = _inventory.ReverseZonesOverlapping(range);
        FreeAddresses = _inventory.FreeAddresses(range);

        var sb = new StringBuilder();
        sb.Append(range.ToString()).Append(": ").Append(range.Count.ToString("N0")).Append(" Adressen · ");
        sb.Append(recordCount).Append(" Einträge verweisen darauf");

        if (range.Count <= 65_536)
        {
            sb.Append(" · ").Append(FreeAddresses.Count.ToString("N0")).Append(" ohne Eintrag");
        }

        sb.Append(reverse.Count switch
        {
            0 => " · keine Reverse-Zone",
            1 => $" · Reverse-Zone {reverse[0].Name}",
            _ => $" · Reverse-Zonen {string.Join(", ", reverse.Select(z => z.Name))}",
        });

        NetworkSummary = sb.ToString();
    }

    /// <summary>Begriff in den Verlauf, wenn der Nutzer Enter drückt.</summary>
    [RelayCommand]
    private void CommitSearch()
    {
        _settings.Current.RememberSearch(SearchText);
        _settings.Save();

        RecentSearches.Clear();
        foreach (string term in _settings.Current.RecentSearches) RecentSearches.Add(term);
        ApplyFilter();
    }

    private bool CanCopyFreeAddresses() => FreeAddresses.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyFreeAddresses))]
    private async Task CopyFreeAddressesAsync()
    {
        if (_shell is null) return;
        await _shell.CopyToClipboardAsync(string.Join(Environment.NewLine, FreeAddresses.Select(Ipv4.ToText))).ConfigureAwait(true);
        StatusText = $"{FreeAddresses.Count} freie Adressen in die Zwischenablage kopiert.";
    }

    private bool CanExportCsv() => VisibleRows.Count > 0;

    /// <summary>Sichtbare Zeilen als CSV — für die Abstimmung mit Kollegen vor dem Aufräumen.</summary>
    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private async Task ExportCsvAsync()
    {
        if (_shell is null) return;

        string? path = await _shell.SaveTextFileAsync($"dns-{DateTime.Now:yyyy-MM-dd}.csv", RecordCsv.Build(VisibleRows))
            .ConfigureAwait(true);
        if (path is not null) StatusText = $"{VisibleRows.Count} Einträge nach {path} exportiert.";
    }

    // ------------------------------------------------------------------
    // Auswahl und Details
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RecordRow? _selectedRow;

    public bool HasSelection => SelectedRow is not null;

    /// <summary>Alle markierten Zeilen (Mehrfachauswahl); setzt das Fenster.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetargetCnameCommand))]
    [NotifyPropertyChangedFor(nameof(SelectionIsCname))]
    private IReadOnlyList<RecordRow> _selectedRows = [];

    public ObservableCollection<RecordRow> RelatedRows { get; } = [];

    partial void OnSelectedRowChanged(RecordRow? value)
    {
        RelatedRows.Clear();
        if (value is null) return;

        foreach (var related in _inventory.Related(value.Record))
        {
            if (_rowsByKey.TryGetValue(related.Key, out var row)) RelatedRows.Add(row);
        }
    }

    /// <summary>Springt zu einem verwandten Eintrag.</summary>
    [RelayCommand]
    private void GoToRow(RecordRow? row)
    {
        if (row is null) return;
        if (!VisibleRows.Contains(row))
        {
            SearchText = row.Name;
            ApplyFilter();
        }
        SelectedRow = row;
    }

    // ------------------------------------------------------------------
    // Start und Fehlerbehandlung
    // ------------------------------------------------------------------

    /// <summary>
    /// Lädt beim Start, zeigt nach einem Update einmal „Was ist neu", sieht nach
    /// einem neuen Update und startet die Prüfung alle 30 Minuten
    /// (MainWindowViewModel.Updates.cs).
    /// </summary>
    public async Task InitializeAsync()
    {
        _sightings.Load();
        await ReloadAsync().ConfigureAwait(true);
        await ShowWhatsNewIfUpdatedAsync().ConfigureAwait(true);
        if (_settings.Current.CheckUpdatesOnStart) await CheckForUpdateAsync(atStartup: true).ConfigureAwait(true);
        StartUpdateLoop();
    }

    /// <summary>Wird nach dem Speichern der Einstellungen gerufen.</summary>
    public async Task OnSettingsChangedAsync()
    {
        // Ohne Invalidate arbeitet das Werkzeug weiter gegen die alte
        // Verbindung, und ein geänderter Server wirkt scheinbar nicht.
        _server.Invalidate();
        ConnectionText = "Nicht verbunden";
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private void Fail(string userMessage, Exception ex)
    {
        Log.Error(ex, "{0}", userMessage);
        ErrorMessage = userMessage;
        StatusText = "Fehler.";
    }

    /// <summary>Speichert offene Zustände beim Beenden.</summary>
    public void PersistOnExit()
    {
        _scanCts?.Cancel();
        _updateLoopCts?.Cancel();
        _sightings.Save();
        _settings.Save();
    }
}
