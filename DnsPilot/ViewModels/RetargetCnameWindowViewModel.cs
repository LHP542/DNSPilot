using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DnsPilot.Models;
using DnsPilot.Services;
using NLog;

namespace DnsPilot.ViewModels;

/// <summary>
/// Dialog „Alias umbiegen": ein CNAME bekommt ein neues Ziel, Name und TTL
/// bleiben. Prüft beim Tippen gegen das Inventar und sichert den bisherigen
/// Eintrag, bevor der Server ihn ändert.
/// </summary>
public sealed partial class RetargetCnameWindowViewModel : ViewModelBase
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private readonly IDnsServer _server;
    private readonly DnsInventory _inventory;
    private readonly Func<DnsRecord, string> _backup;

    /// <param name="backup">Sichert den bisherigen Eintrag und liefert die Sicherungsdatei; wirft bei Fehlern.</param>
    public RetargetCnameWindowViewModel(IDnsServer server, DnsInventory inventory, DnsRecord alias, Func<DnsRecord, string> backup)
    {
        _server = server;
        _inventory = inventory;
        _backup = backup;
        Alias = alias;

        // Vorschläge: alle Hostnamen und Aliase der eigenen Vorwärtszonen.
        Suggestions = inventory.Records
            .Where(r => r.Type is RecordType.A or RecordType.AAAA or RecordType.CNAME
                && !r.OwnerName.Equals(alias.OwnerName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.OwnerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = inventory.RecordsNamed(alias.Data);
        CurrentTargetText = inventory.ForwardZoneFor(alias.Data) is null
            ? $"{alias.Data}  (außerhalb der eigenen Zonen)"
            : current.Count == 0
                ? $"{alias.Data}  (existiert nicht)"
                : $"{alias.Data}  ({string.Join(", ", current.Select(r => $"{r.Type} {r.Data}"))})";

        Revalidate();
    }

    public DnsRecord Alias { get; }

    public string AliasName => Alias.OwnerName;

    public string CurrentTargetText { get; }

    public IReadOnlyList<string> Suggestions { get; }

    [ObservableProperty] private string _target = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetargetCommand))]
    private bool _acceptWarnings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetargetCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetargetCommand))]
    private CnameTargetCheck _check = new(null, [], [], null);

    [ObservableProperty] private string? _resultMessage;

    public string ErrorsText => string.Join("\n", Check.Errors);

    public string WarningsText => string.Join("\n", Check.Warnings);

    /// <summary>Fehler erst zeigen, wenn etwas eingegeben ist — ein leeres Feld ist kein Fehler.</summary>
    public bool HasErrors => Check.Errors.Count > 0 && Target.Trim().Length > 0;

    public bool HasWarnings => Check.Warnings.Count > 0;

    public string? InfoText => Check.Info;

    public string Preview => Check.Fqdn is { } fqdn && Check.IsValid ? $"{Alias.OwnerName}  →  {fqdn}" : string.Empty;

    /// <summary>Der geänderte Eintrag, wenn erfolgreich; sonst null.</summary>
    public DnsRecord? Updated { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnTargetChanged(string value) => Revalidate();

    partial void OnCheckChanged(CnameTargetCheck value)
    {
        OnPropertyChanged(nameof(ErrorsText));
        OnPropertyChanged(nameof(WarningsText));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(Preview));
    }

    private void Revalidate()
    {
        Check = CnameTargetValidator.Check(_inventory, Alias, Target);
        AcceptWarnings = false;
    }

    private bool CanRetarget() => !IsBusy && Check.IsValid && (Check.Warnings.Count == 0 || AcceptWarnings);

    [RelayCommand(CanExecute = nameof(CanRetarget))]
    private async Task RetargetAsync()
    {
        string target = Check.Fqdn!;
        IsBusy = true;
        ResultMessage = null;
        try
        {
            // Wie beim Löschen: ohne Sicherung wird nicht geändert.
            string file = _backup(Alias);
            Log.Info("Vor dem Umbiegen gesichert: {0} → {1}", Alias, file);

            Updated = await _server.RetargetCnameAsync(Alias, target, CancellationToken.None).ConfigureAwait(true);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Sicherung vor dem Umbiegen von {0} fehlgeschlagen.", Alias);
            ResultMessage = "Der bisherige Eintrag ließ sich nicht sichern — nichts geändert. Details stehen im Log.";
        }
        catch (DnsAccessException ex)
        {
            Log.Warn(ex, "Umbiegen von {0} auf {1} fehlgeschlagen.", Alias, target);
            ResultMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
