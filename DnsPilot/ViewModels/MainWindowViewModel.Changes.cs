using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.ViewModels;

/// <summary>Einträge anlegen und löschen.</summary>
public sealed partial class MainWindowViewModel
{
    private readonly DeletionBackup _backup;

    /// <summary>
    /// Schreibschutz. Bewusst nicht gespeichert: jede Sitzung beginnt lesend,
    /// Ändern ist eine Entscheidung für den Moment, keine Grundeinstellung.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateMissingPtrCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetargetCnameCommand))]
    [NotifyPropertyChangedFor(nameof(PtrFixTip))]
    private bool _allowChanges;

    partial void OnAllowChangesChanged(bool value) =>
        Log.Info(value ? "Änderungen erlaubt (Schreibmodus an)." : "Schreibmodus aus.");

    private bool CanNewHost() => AllowChanges && !IsBusy && _inventory.ForwardZones.Any(z => z.IsWritable);

    [RelayCommand(CanExecute = nameof(CanNewHost))]
    private async Task NewHostAsync()
    {
        if (_shell is null) return;

        var vm = new NewHostWindowViewModel(_server, _inventory, _settings.Current.DefaultTtl, SuggestAddress());
        await _shell.ShowNewHostDialogAsync(vm).ConfigureAwait(true);

        if (vm.Created is not { Count: > 0 } created) return;

        SetInventory(_inventory.With(added: created));
        SearchText = created[0].OwnerName;
        ApplyFilter();
        SelectedRow = VisibleRows.FirstOrDefault(r => r.Record.Key == created[0].Key);
        StatusText = $"{created[0].OwnerName} angelegt ({string.Join(" + ", created.Select(r => r.Type))}).";
    }

    /// <summary>
    /// Vorschlag für die Adresse: die gesuchte, wenn sie frei ist, sonst die
    /// erste freie im gesuchten Netz.
    /// </summary>
    private string? SuggestAddress()
    {
        var query = SearchQuery.Parse(SearchText);
        if (query.Range is not { } range) return null;

        var free = _inventory.FreeAddresses(range);
        return free.Count > 0 ? Ipv4.ToText(free[0]) : null;
    }

    private bool CanDeleteSelected() => AllowChanges && !IsBusy && !IsScanning && SelectedRows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        if (_shell is null) return;

        var vm = new DeleteWindowViewModel(SelectedRows.Select(r => r.Record).ToList(), _inventory);
        await _shell.ShowDeleteDialogAsync(vm).ConfigureAwait(true);
        if (!vm.Confirmed) return;

        await DeleteAsync(vm.RecordsToDelete).ConfigureAwait(true);
    }

    /// <summary>
    /// Löscht nacheinander, jeden Eintrag erst nach erfolgreicher Sicherung.
    /// Ein Fehler bei einem Eintrag hält die übrigen nicht auf — am Ende steht,
    /// was geklappt hat und was nicht.
    /// </summary>
    internal async Task DeleteAsync(IReadOnlyList<DnsRecord> records)
    {
        IsBusy = true;
        ErrorMessage = null;

        var deleted = new List<DnsRecord>();
        var failed = new List<string>();
        string server = _serverInfo?.ServerName ?? "?";
        string? backupFile = null;

        try
        {
            foreach (var record in records)
            {
                StatusText = $"Lösche {record.OwnerName} ({deleted.Count + failed.Count + 1}/{records.Count}) …";
                try
                {
                    backupFile = _backup.Write(record, server, DateTime.UtcNow);
                    await _server.DeleteRecordAsync(record, CancellationToken.None).ConfigureAwait(true);
                    deleted.Add(record);
                }
                catch (DnsAccessException ex)
                {
                    Log.Warn(ex, "Löschen von {0} fehlgeschlagen.", record);
                    failed.Add($"{record.OwnerName} ({record.Type}): {ex.Message}");
                }
                catch (IOException ex)
                {
                    // Ohne Sicherung wird nicht gelöscht.
                    Log.Error(ex, "Sicherung vor dem Löschen von {0} fehlgeschlagen.", record);
                    failed.Add($"{record.OwnerName}: Sicherung nicht möglich, nicht gelöscht.");
                }
            }
        }
        finally
        {
            if (deleted.Count > 0) SetInventory(_inventory.With(removed: deleted));
            IsBusy = false;
        }

        StatusText = $"{deleted.Count} von {records.Count} Einträgen gelöscht."
            + (backupFile is null ? string.Empty : $" Sicherung: {backupFile}");

        if (failed.Count > 0)
        {
            ErrorMessage = $"{failed.Count} Einträge nicht gelöscht:\n" + string.Join("\n", failed.Take(8))
                + (failed.Count > 8 ? $"\n… und {failed.Count - 8} weitere (siehe Log)." : string.Empty);
        }
    }

    // ------------------------------------------------------------------
    // Fehlenden PTR anlegen
    // ------------------------------------------------------------------

    /// <summary>PTR, die sich für die markierten Zeilen eindeutig anlegen lassen.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateMissingPtrCommand))]
    [NotifyPropertyChangedFor(nameof(HasPtrFixes), nameof(PtrFixLabel), nameof(PtrFixTip))]
    private IReadOnlyList<PtrFix> _ptrFixes = [];

    public bool HasPtrFixes => PtrFixes.Count > 0;

    public string PtrFixLabel => PtrFixes.Count > 1 ? $"PTR anlegen ({PtrFixes.Count})" : "PTR anlegen";

    public string PtrFixTip
    {
        get
        {
            if (PtrFixes.Count == 0)
            {
                return "Fehlenden PTR für die markierten A-Einträge anlegen. Angeboten nur, wenn kein PTR existiert "
                    + "und kein anderer Name auf dieselbe Adresse zeigt.";
            }

            var sb = new StringBuilder("Legt an:");
            foreach (var fix in PtrFixes.Take(10)) sb.Append('\n').Append(fix);
            if (PtrFixes.Count > 10) sb.Append("\n… und ").Append(PtrFixes.Count - 10).Append(" weitere");
            if (!AllowChanges) sb.Append("\n\nErst „Änderungen erlauben“ einschalten.");
            return sb.ToString();
        }
    }

    partial void OnSelectedRowsChanged(IReadOnlyList<RecordRow> value) => RefreshPtrFixes();

    private void RefreshPtrFixes() =>
        PtrFixes = PtrFixPlanner.Plan(_inventory, SelectedRows.Select(r => r.Record));

    private bool CanCreateMissingPtr() => AllowChanges && !IsBusy && !IsScanning && PtrFixes.Count > 0;

    /// <summary>
    /// Legt die fehlenden PTR der Auswahl an, nacheinander. Ein Fehler bei einem
    /// Eintrag hält die übrigen nicht auf.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateMissingPtr))]
    private async Task CreateMissingPtrAsync()
    {
        var fixes = PtrFixes.ToList();
        IsBusy = true;
        ErrorMessage = null;

        var created = new List<DnsRecord>();
        var failed = new List<string>();
        try
        {
            foreach (var fix in fixes)
            {
                StatusText = $"Lege PTR für {fix.Host.OwnerName} an ({created.Count + failed.Count + 1}/{fixes.Count}) …";
                try
                {
                    created.Add(await _server.CreatePtrAsync(fix.Host, fix.ReverseZone.Name, CancellationToken.None).ConfigureAwait(true));
                }
                catch (DnsAccessException ex)
                {
                    Log.Warn(ex, "PTR für {0} nicht angelegt.", fix);
                    failed.Add($"{fix.Host.OwnerName}: {ex.Message}");
                }
            }
        }
        finally
        {
            if (created.Count > 0) SetInventory(_inventory.With(added: created));
            IsBusy = false;
        }

        StatusText = created.Count == 1 && fixes.Count == 1
            ? $"PTR angelegt: {created[0].Address} → {created[0].Data}."
            : $"{created.Count} von {fixes.Count} PTR angelegt.";

        if (failed.Count > 0)
        {
            ErrorMessage = $"{failed.Count} PTR nicht angelegt:\n" + string.Join("\n", failed.Take(8))
                + (failed.Count > 8 ? $"\n… und {failed.Count - 8} weitere (siehe Log)." : string.Empty);
        }
    }

    // ------------------------------------------------------------------
    // Alias umbiegen
    // ------------------------------------------------------------------

    /// <summary>Genau ein CNAME markiert — dann gibt es „Alias umbiegen".</summary>
    public bool SelectionIsCname => SelectedRows is [{ Record.Type: RecordType.CNAME }];

    private bool CanRetargetCname() => AllowChanges && !IsBusy && !IsScanning && SelectionIsCname;

    [RelayCommand(CanExecute = nameof(CanRetargetCname))]
    private async Task RetargetCnameAsync()
    {
        if (_shell is null) return;

        var alias = SelectedRows[0].Record;
        string server = _serverInfo?.ServerName ?? "?";
        var vm = new RetargetCnameWindowViewModel(_server, _inventory, alias,
            record => _backup.Write(record, server, DateTime.UtcNow));
        await _shell.ShowRetargetCnameDialogAsync(vm).ConfigureAwait(true);

        if (vm.Updated is { } updated) ApplyRetarget(alias, updated);
    }

    /// <summary>Übernimmt einen umgebogenen Alias ins Inventar und wählt ihn wieder aus.</summary>
    internal void ApplyRetarget(DnsRecord old, DnsRecord updated)
    {
        SetInventory(_inventory.With(added: [updated], removed: [old]));
        SelectedRow = VisibleRows.FirstOrDefault(r => r.Record.Key == updated.Key);
        StatusText = $"{old.OwnerName} zeigt jetzt auf {updated.Data} (vorher {old.Data}).";
    }

    /// <summary>Öffnet den Ordner mit den Löschsicherungen.</summary>
    [RelayCommand]
    private async Task OpenBackupFolderAsync()
    {
        if (_shell is null) return;
        Directory.CreateDirectory(_backup.Folder);
        await _shell.OpenExternalAsync(new Uri(_backup.Folder).AbsoluteUri).ConfigureAwait(true);
    }
}
