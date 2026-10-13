using CommunityToolkit.Mvvm.ComponentModel;
using DnsPilot.Models;
using DnsPilot.Services;

namespace DnsPilot.ViewModels;

/// <summary>
/// Eine Zeile der Eintragstabelle: der Eintrag, seine Befunde aus der Analyse
/// und — sobald geprüft — Erreichbarkeit und Auflösung.
/// </summary>
public sealed partial class RecordRow : ObservableObject
{
    public RecordRow(DnsRecord record, IReadOnlyList<Finding> findings)
    {
        Record = record;
        Findings = findings;
        SearchBlob = $"{record.OwnerName} {record.Address} {record.Data} {record.Zone} {record.Type}".ToLowerInvariant();
    }

    public DnsRecord Record { get; }

    public IReadOnlyList<Finding> Findings { get; }

    /// <summary>Kleingeschriebener Suchtext über alle sichtbaren Spalten.</summary>
    internal string SearchBlob { get; }

    /// <summary>
    /// Anzeigename. Bei PTR die Adresse statt <c>55.2.10.10.in-addr.arpa</c> —
    /// so liest sich die Zeile als „10.10.2.55 PTR → scanner-keller".
    /// </summary>
    public string Name => Record.Type == RecordType.PTR && Record.Address is { } ip ? ip.ToString() : Record.OwnerName;

    /// <summary>Vollständiger Eintragsname, wie er im DNS steht.</summary>
    public string OwnerName => Record.OwnerName;

    /// <summary>
    /// Sortierschlüssel für die Namensspalte: Hosts alphabetisch, PTR dahinter
    /// und numerisch nach Adresse.
    /// </summary>
    public string NameSortKey => Record.Type == RecordType.PTR && Ipv4.TryToUInt32(Record.Address, out uint ip)
        ? "~" + ip.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)
        : Record.OwnerName.ToLowerInvariant();

    public string HostLabel => Record.HostLabel;

    public string TypeText => Record.Type.ToString();

    public string Data => Record.Data;

    public string Zone => Record.Zone;

    public string TtlText => Record.Ttl >= 3600 && Record.Ttl % 3600 == 0
        ? $"{Record.Ttl / 3600} h"
        : $"{Record.Ttl} s";

    public string TimestampText => Record.TimestampUtc is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy") : "statisch";

    /// <summary>
    /// Sortierschlüssel für die Daten-Spalte. Adressen alphabetisch sortiert
    /// ergeben 10.0.0.100 vor 10.0.0.2 — deshalb Oktette auf drei Stellen.
    /// </summary>
    public string DataSortKey => Ipv4.TryToUInt32(Record.Address, out uint ip) && Record.Type != RecordType.PTR
        ? ip.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)
        : Record.Data.ToLowerInvariant();

    /// <summary>Sortierschlüssel für die Zeitstempel-Spalte; statisch zuletzt.</summary>
    public long TimestampSortKey => Record.TimestampUtc?.Ticks ?? long.MaxValue;

    public Severity Severity => Findings.Count == 0 ? Severity.None : Findings.Max(f => f.Severity);

    public string FindingsText => string.Join(" · ", Findings.Select(f => f.Text));

    public bool HasFindings => Findings.Count > 0;

    public bool HasError => Severity == Severity.Error;

    public bool HasWarning => Severity == Severity.Warning;

    public bool HasInfo => Severity == Severity.Info;

    // ------------------------------------------------------------------
    // Prüfergebnis
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsOffline), nameof(IsCandidate), nameof(IsUnchecked),
        nameof(NeedsAttention), nameof(ReachabilitySortKey))]
    private Reachability _reachability = Reachability.Unchecked;

    [ObservableProperty]
    private string _reachabilityText = "ungeprüft";

    [ObservableProperty]
    private HostSighting? _sighting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResolveText))]
    private string? _resolveText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    private bool _resolveProblem;

    public bool IsOnline => Reachability == Reachability.Online;

    public bool IsOffline => Reachability == Reachability.Offline;

    public bool IsCandidate => Reachability == Reachability.Candidate;

    public bool IsUnchecked => Reachability == Reachability.Unchecked;

    public bool HasResolveText => !string.IsNullOrEmpty(ResolveText);

    /// <summary>Kandidaten zuerst, dann offline, ungeprüft, online.</summary>
    public int ReachabilitySortKey => Reachability switch
    {
        Reachability.Candidate => 0,
        Reachability.Offline => 1,
        Reachability.Unchecked => 2,
        _ => 3,
    };

    /// <summary>Für den Filter „nur Auffälligkeiten".</summary>
    public bool NeedsAttention => Severity >= Severity.Warning || IsCandidate || ResolveProblem;

    /// <summary>Übernimmt die Historie und rechnet das Urteil neu.</summary>
    public void ApplySighting(HostSighting? sighting, DateTime nowUtc, int candidateAfterDays, int minChecks)
    {
        Sighting = sighting;
        Reachability = SightingEvaluator.Evaluate(sighting, nowUtc, candidateAfterDays, minChecks);
        ReachabilityText = SightingEvaluator.Describe(sighting, Reachability);
        OnPropertyChanged(nameof(SightingDetails));
    }

    /// <summary>Ausführliche Historie für die Detailansicht.</summary>
    public string SightingDetails => Sighting is not { } s
        ? "Diese Adresse wurde noch nie geprüft."
        : (s.Checks == 1
              ? $"1 Prüfung am {s.LastCheckedUtc.ToLocalTime():dd.MM.yyyy HH:mm}"
              : $"{s.Checks} Prüfungen seit {s.FirstCheckedUtc.ToLocalTime():dd.MM.yyyy}, letzte {s.LastCheckedUtc.ToLocalTime():dd.MM.yyyy HH:mm}")
          + (s.LastOnlineUtc is { } online
              ? $"\nZuletzt erreicht {online.ToLocalTime():dd.MM.yyyy HH:mm} ({s.LastMethod})"
              : "\nNoch nie erreicht")
          + (s.FailuresSinceOnline > 1 ? $"\n{s.FailuresSinceOnline} Prüfungen ohne Antwort in Folge" : string.Empty);

    /// <summary>
    /// Vergleicht die Auflösung über den Resolver mit dem, was im Eintrag steht.
    /// </summary>
    public void ApplyResolve(ResolveResult? resolve)
    {
        if (resolve is null) return;

        switch (Record.Type)
        {
            case RecordType.A or RecordType.AAAA:
                if (!resolve.Resolved)
                {
                    SetResolve($"Name {resolve.Error ?? "nicht auflösbar"}", problem: true);
                }
                else if (resolve.Addresses.Contains(Record.Data, StringComparer.OrdinalIgnoreCase))
                {
                    SetResolve(resolve.Addresses.Count == 1
                        ? "Auflösung stimmt"
                        : $"Auflösung stimmt (liefert auch {string.Join(", ", resolve.Addresses.Where(a => a != Record.Data))})", problem: false);
                }
                else
                {
                    SetResolve($"Name löst auf {string.Join(", ", resolve.Addresses)} auf", problem: true);
                }
                break;

            case RecordType.PTR:
                if (!resolve.Resolved)
                {
                    SetResolve($"Rückwärtsauflösung {resolve.Error ?? "fehlgeschlagen"}", problem: true);
                }
                else if (resolve.Addresses.Any(n => n.TrimEnd('.').Equals(Record.Data, StringComparison.OrdinalIgnoreCase)))
                {
                    SetResolve("Rückwärtsauflösung stimmt", problem: false);
                }
                else
                {
                    // Bei mehreren PTRs auf eine Adresse liefert der Resolver nur
                    // einen davon — das ist ein Hinweis, kein Fehler dieses Eintrags.
                    SetResolve($"Rückwärtsauflösung liefert {resolve.Addresses[0]}", problem: false);
                }
                break;

            case RecordType.CNAME:
                SetResolve(resolve.Resolved
                    ? $"Alias löst auf {string.Join(", ", resolve.Addresses)} auf"
                    : $"Alias {resolve.Error ?? "nicht auflösbar"}", problem: !resolve.Resolved);
                break;
        }
    }

    private void SetResolve(string text, bool problem)
    {
        ResolveText = text;
        ResolveProblem = problem;
    }
}
