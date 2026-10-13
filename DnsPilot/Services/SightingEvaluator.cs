using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>
/// Leitet aus der Prüfhistorie einer Adresse das Urteil ab.
/// </summary>
public static class SightingEvaluator
{
    /// <summary>
    /// Kandidat wird nur, was <b>beide</b> Bedingungen erfüllt: seit mindestens
    /// <paramref name="candidateAfterDays"/> Tagen nicht erreicht und mindestens
    /// <paramref name="minChecks"/> Prüfungen ohne Antwort in dieser Zeit. Nur die
    /// Zeit reicht nicht — eine einzige Prüfung vor 40 Tagen ist keine
    /// Beobachtung über 40 Tage.
    /// </summary>
    public static Reachability Evaluate(HostSighting? sighting, DateTime nowUtc, int candidateAfterDays, int minChecks)
    {
        if (sighting is null || sighting.Checks == 0) return Reachability.Unchecked;
        if (sighting.LastWasOnline) return Reachability.Online;

        DateTime silentSince = sighting.LastOnlineUtc ?? sighting.FirstCheckedUtc;
        bool longEnough = (nowUtc - silentSince).TotalDays >= candidateAfterDays;
        bool enoughChecks = sighting.FailuresSinceOnline >= minChecks;

        return longEnough && enoughChecks ? Reachability.Candidate : Reachability.Offline;
    }

    /// <summary>Kurzer Anzeigetext, z. B. „online (ICMP)" oder „nie erreicht seit 12.09.26".</summary>
    public static string Describe(HostSighting? sighting, Reachability verdict) => verdict switch
    {
        Reachability.Unchecked => "ungeprüft",
        Reachability.Online => $"online ({sighting?.LastMethod ?? "?"})",
        _ when sighting?.LastOnlineUtc is { } online => $"zuletzt online {online.ToLocalTime():dd.MM.yy}",
        _ => $"nie erreicht seit {sighting!.FirstCheckedUtc.ToLocalTime():dd.MM.yy}",
    };
}
