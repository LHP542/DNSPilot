using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsPilot.Models;
using DnsPilot.Services;
using NLog;

namespace DnsPilot.ViewModels;

/// <summary>
/// Dialog „Neuer Host": A-Eintrag plus passender PTR in einem Schritt. Prüft
/// beim Tippen gegen das Inventar, damit doppelte Adressen auffallen, bevor
/// sie angelegt sind.
/// </summary>
public sealed partial class NewHostWindowViewModel : ViewModelBase
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly IDnsServer _server;
    private readonly DnsInventory _inventory;

    public NewHostWindowViewModel(IDnsServer server, DnsInventory inventory, uint defaultTtl, string? suggestedAddress)
    {
        _server = server;
        _inventory = inventory;

        foreach (var zone in inventory.ForwardZones.Where(z => z.IsWritable && !z.Name.StartsWith('_')))
        {
            Zones.Add(zone.Name);
        }

        _selectedZone = Zones.FirstOrDefault();
        _address = suggestedAddress ?? string.Empty;
        _ttlText = defaultTtl.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Revalidate();
    }

    public ObservableCollection<string> Zones { get; } = [];

    [ObservableProperty] private string? _selectedZone;
    [ObservableProperty] private string _hostLabel = string.Empty;
    [ObservableProperty] private string _address;
    [ObservableProperty] private string _ttlText;
    [ObservableProperty] private bool _createPtr = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _acceptWarnings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private NewHostCheck _check = new([], []);

    [ObservableProperty] private string? _reverseZoneText;
    [ObservableProperty] private bool _canCreatePtr;
    [ObservableProperty] private string? _resultMessage;

    public string ErrorsText => string.Join("\n", Check.Errors);

    public string WarningsText => string.Join("\n", Check.Warnings);

    public bool HasErrors => Check.Errors.Count > 0 && (HostLabel.Length > 0 || Address.Length > 0);

    public bool HasWarnings => Check.Warnings.Count > 0;

    public string Preview => SelectedZone is null || HostLabel.Trim().Length == 0
        ? string.Empty
        : $"{HostLabel.Trim()}.{SelectedZone}  →  {Address.Trim()}";

    /// <summary>Die angelegten Einträge, wenn erfolgreich; sonst null.</summary>
    public IReadOnlyList<DnsRecord>? Created { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnSelectedZoneChanged(string? value) => Revalidate();

    partial void OnHostLabelChanged(string value) => Revalidate();

    partial void OnAddressChanged(string value) => Revalidate();

    partial void OnCheckChanged(NewHostCheck value)
    {
        OnPropertyChanged(nameof(ErrorsText));
        OnPropertyChanged(nameof(WarningsText));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(Preview));
    }

    private void Revalidate()
    {
        Check = NewHostValidator.Check(_inventory, SelectedZone, HostLabel, Address);
        AcceptWarnings = false;

        if (Ipv4.TryParseStrict(Address, out uint ip) && _inventory.ReverseZoneFor(ip) is { } zone)
        {
            ReverseZoneText = $"PTR {ReverseZone.PtrOwnerName(ip)} in {zone.Name}";
            CanCreatePtr = zone.IsWritable;
        }
        else
        {
            ReverseZoneText = "Keine Reverse-Zone für diese Adresse.";
            CanCreatePtr = false;
        }
    }

    private bool CanCreate() => !IsBusy && Check.IsValid && (Check.Warnings.Count == 0 || AcceptWarnings);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (!uint.TryParse(TtlText, out uint ttl) || ttl is 0 or > 604_800)
        {
            ResultMessage = "Die TTL muss zwischen 1 und 604800 Sekunden (7 Tage) liegen.";
            return;
        }

        Ipv4.TryParseStrict(Address, out uint ip);
        string? reverse = CreatePtr && CanCreatePtr ? _inventory.ReverseZoneFor(ip)?.Name : null;
        var request = new NewHostRequest(SelectedZone!, HostLabel.Trim().TrimEnd('.'), Ipv4.FromUInt32(ip), reverse, ttl);

        IsBusy = true;
        ResultMessage = null;
        try
        {
            Created = await _server.CreateHostAsync(request, CancellationToken.None).ConfigureAwait(true);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (DnsAccessException ex)
        {
            Log.Warn(ex, "Anlegen von {0} fehlgeschlagen.", request.Fqdn);
            ResultMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Für Tests: die Adresse als <see cref="IPAddress"/>.</summary>
    internal IPAddress? ParsedAddress => Ipv4.TryParseStrict(Address, out uint ip) ? Ipv4.FromUInt32(ip) : null;
}
