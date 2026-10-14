using DnsPilot.Models;

namespace DnsPilot.Services;

/// <summary>
/// Zugriff auf den DNS-Server. Als Schnittstelle, damit Oberfläche und Tests
/// ohne echten Server laufen (<see cref="DemoDnsServer"/>).
/// </summary>
public interface IDnsServer
{
    /// <summary>Baut die Verbindung auf (oder liefert die bestehende).</summary>
    Task<DnsServerInfo> ConnectAsync(CancellationToken token);

    /// <summary>Lädt alle Zonen und die Einträge vom Typ A, AAAA, CNAME und PTR.</summary>
    Task<DnsSnapshot> LoadAsync(IProgress<string>? progress, CancellationToken token);

    /// <summary>Legt einen A-Eintrag und optional den PTR an. Liefert die angelegten Einträge.</summary>
    Task<IReadOnlyList<DnsRecord>> CreateHostAsync(NewHostRequest request, CancellationToken token);

    /// <summary>
    /// Legt für einen bestehenden A-Eintrag den fehlenden PTR in
    /// <paramref name="reverseZone"/> an, mit derselben TTL. Ist der A-Eintrag
    /// dynamisch, altert der PTR mit (Scavenging räumt beide gemeinsam weg) —
    /// ein statischer PTR zu einem dynamischen Host bliebe sonst als Überrest
    /// zurück. Liefert den angelegten Eintrag.
    /// </summary>
    Task<DnsRecord> CreatePtrAsync(DnsRecord host, string reverseZone, CancellationToken token);

    /// <summary>
    /// Biegt einen CNAME auf ein neues Ziel um; Name und TTL bleiben. Wirft
    /// <see cref="DnsAccessException"/>, wenn der Alias auf dem Server nicht
    /// mehr mit dem bisherigen Ziel existiert. Liefert den geänderten Eintrag.
    /// </summary>
    Task<DnsRecord> RetargetCnameAsync(DnsRecord alias, string newTargetFqdn, CancellationToken token);

    /// <summary>
    /// Löscht genau diesen Eintrag. Wirft <see cref="DnsAccessException"/>, wenn
    /// er auf dem Server nicht mehr existiert.
    /// </summary>
    Task DeleteRecordAsync(DnsRecord record, CancellationToken token);

    /// <summary>Verwirft die Verbindung, z. B. nach geänderten Einstellungen.</summary>
    void Invalidate();
}

/// <summary>
/// Fehler beim Zugriff auf den DNS-Server, mit einer Meldung, die man dem
/// Nutzer so zeigen kann.
/// </summary>
public sealed class DnsAccessException(string message, Exception? inner = null) : Exception(message, inner);
