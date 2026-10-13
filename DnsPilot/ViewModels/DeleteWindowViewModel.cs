using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.ViewModels;

/// <summary>Eine Zeile der Löschliste.</summary>
public sealed partial class DeleteItem(DnsRecord record, string reason, bool include) : ObservableObject
{
    public DnsRecord Record { get; } = record;

    public string Reason { get; } = reason;

    public string Text => $"{Record.OwnerName}  {Record.Type}  {Record.Data}";

    [ObservableProperty]
    private bool _include = include;
}

/// <summary>
/// Bestätigung vor dem Löschen. Listet jeden Eintrag einzeln und schlägt die
/// PTRs mit vor, die zu gelöschten A-Einträgen gehören — sonst bleiben genau
/// die verwaisten PTRs zurück, wegen denen man aufräumt.
/// </summary>
public sealed partial class DeleteWindowViewModel : ViewModelBase
{
    public DeleteWindowViewModel(IReadOnlyList<DnsRecord> selected, DnsInventory inventory)
    {
        var keys = selected.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var record in selected)
        {
            var item = new DeleteItem(record, "ausgewählt", include: true);
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CountText));
            Items.Add(item);
        }

        foreach (var a in selected.Where(r => r.Type == RecordType.A))
        {
            if (!Ipv4.TryToUInt32(a.Address, out uint ip)) continue;

            foreach (var ptr in inventory.PtrRecordsAt(ip))
            {
                if (keys.Contains(ptr.Key)) continue;
                if (!ptr.Data.Equals(a.OwnerName, StringComparison.OrdinalIgnoreCase)) continue;

                keys.Add(ptr.Key);
                var item = new DeleteItem(ptr, $"PTR zu {a.OwnerName}", include: true);
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CountText));
                Items.Add(item);
            }
        }
    }

    public List<DeleteItem> Items { get; } = [];

    public string CountText => $"{Items.Count(i => i.Include)} von {Items.Count} Einträgen werden gelöscht.";

    /// <summary>Löschen geht erst, wenn die Liste ausdrücklich bestätigt ist.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _reviewed;

    public bool Confirmed { get; private set; }

    public IReadOnlyList<DnsRecord> RecordsToDelete => Items.Where(i => i.Include).Select(i => i.Record).ToList();

    public event EventHandler? CloseRequested;

    private bool CanDelete() => Reviewed && Items.Any(i => i.Include);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        Confirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
