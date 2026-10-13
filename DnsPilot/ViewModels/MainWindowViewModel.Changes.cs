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

    /// <summary>Öffnet den Ordner mit den Löschsicherungen.</summary>
    [RelayCommand]
    private async Task OpenBackupFolderAsync()
    {
        if (_shell is null) return;
        Directory.CreateDirectory(_backup.Folder);
        await _shell.OpenExternalAsync(new Uri(_backup.Folder).AbsoluteUri).ConfigureAwait(true);
    }
}
