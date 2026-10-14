using System.Diagnostics;
using DnsPilot.Models;
using NLog;

namespace DnsPilot.Services;

/// <summary>
/// DNS-Server über das PowerShell-Modul <c>DnsServer</c> (RSAT-DNS-Tools).
///
/// <b>Warum nicht WMI:</b> Der erste Ansatz ging über <c>root\MicrosoftDNS</c>
/// per DCOM — auf dem Jumphost mit dem Admin-Konto „Zugriff verweigert",
/// obwohl die DNS-Konsole funktionierte (2026-10-13). Die Konsole spricht ein
/// eigenes RPC-Protokoll; <c>Get-DnsServerResourceRecord -ComputerName</c> lief
/// mit demselben Konto. <c>dnscmd</c> wäre der dritte Weg gewesen, liefert aber
/// lokalisierten Text und bräuchte einen Prozess pro Zone.
/// </summary>
public sealed class PowerShellDnsServer(SettingsService settings, IPowerShellRunner runner) : IDnsServer
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private const int ExitModuleMissing = 2;
    private const int ExitNotFound = 3;

    private readonly SettingsService _settings = settings;
    private readonly IPowerShellRunner _runner = runner;
    private DnsServerInfo? _info;

    /// <summary>Server aus den Einstellungen, sonst der Anmeldeserver dieser Sitzung.</summary>
    public static string ResolveServerName(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        string? logon = Environment.GetEnvironmentVariable("LOGONSERVER")?.TrimStart('\\');
        if (!string.IsNullOrWhiteSpace(logon)) return logon;

        throw new DnsAccessException(
            "Kein DNS-Server eingestellt und kein Anmeldeserver erkennbar. Bitte in den Einstellungen einen Server eintragen (z. B. DC01).");
    }

    private string Server => ResolveServerName(_settings.Current.Server);

    public async Task<DnsServerInfo> ConnectAsync(CancellationToken token)
    {
        if (_info is not null) return _info;

        string server = Server;
        Log.Info("Prüfe Zugriff auf {0} über das PowerShell-Modul DnsServer.", server);
        var sw = Stopwatch.StartNew();

        var result = await RunAsync(Scripts.Connect, server, null, token).ConfigureAwait(false);
        Log.Info("Zugriff auf {0} steht ({1} ms, {2}).", server, sw.ElapsedMilliseconds, string.Join(" ", result.Output));

        _info = CreateInfo(server);
        return _info;
    }

    private DnsServerInfo CreateInfo(string server)
    {
        string? user = _settings.Current.AdminUser?.Trim();
        return new DnsServerInfo(server, string.IsNullOrEmpty(user) ? $@"{Environment.UserDomainName}\{Environment.UserName}" : user);
    }

    public async Task<DnsSnapshot> LoadAsync(IProgress<string>? progress, CancellationToken token)
    {
        // Bewusst KEIN ConnectAsync vorweg: das fragt die Zonen ab, und das
        // Lese-Skript tut es ohnehin — auf dem Jumphost kostete die doppelte
        // Abfrage 6,8 s bei jedem Start (2026-10-13). Ein erfolgreiches Laden
        // gilt als bestandene Verbindungsprüfung.
        string server = Server;
        var sw = Stopwatch.StartNew();

        int total = 0, done = 0;
        void OnLine(string line)
        {
            if (line.StartsWith("N\t", StringComparison.Ordinal)) int.TryParse(line.AsSpan(2), out total);
            else if (line.StartsWith("P\t", StringComparison.Ordinal))
            {
                int n = Interlocked.Increment(ref done);
                progress?.Report($"Zone {n} von {total} geladen ({line[2..]}) …");
            }
        }

        progress?.Report("Zonen werden abgefragt …");
        var result = await RunAsync(Scripts.Load, server, OnLine, token).ConfigureAwait(false);

        var zones = new List<DnsZone>();
        var records = new List<DnsRecord>();
        var failedZones = new List<string>();

        foreach (string line in result.Output)
        {
            switch (line.Length > 1 ? line[..2] : line)
            {
                case "Z\t":
                    if (DnsRecordMapper.ParseZone(line) is { } zone) zones.Add(zone);
                    break;
                case "R\t":
                    if (DnsRecordMapper.ParseRecord(line) is { } record) records.Add(record);
                    break;
                case "E\t":
                    failedZones.Add(line[2..].Replace('\t', ':'));
                    break;
            }
        }

        // Einträge nur aus Zonen, die angezeigt werden.
        var zoneNames = zones.Select(z => z.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        records.RemoveAll(r => !zoneNames.Contains(r.Zone));

        foreach (string failure in failedZones) Log.Warn("Zone nicht lesbar: {0}", failure);
        Log.Info("{0} Zonen, {1} Einträge (A/AAAA/CNAME/PTR) von {2} in {3:F1} s geladen; {4} Zonen nicht lesbar.",
            zones.Count, records.Count, server, sw.Elapsed.TotalSeconds, failedZones.Count);

        if (failedZones.Count > 0)
        {
            progress?.Report($"{failedZones.Count} Zonen konnten nicht gelesen werden — Details im Log.");
        }

        _info = CreateInfo(server);
        return new DnsSnapshot(zones, records, DateTime.UtcNow, sw.Elapsed);
    }

    public async Task<IReadOnlyList<DnsRecord>> CreateHostAsync(NewHostRequest request, CancellationToken token)
    {
        string server = Server;
        string ip = request.Address.ToString();
        string? ptrNode = null;
        if (request.ReverseZone is { } rz && Ipv4.TryToUInt32(request.Address, out uint value))
        {
            ptrNode = DnsRecordMapper.NodeName(ReverseZone.PtrOwnerName(value), rz);
        }

        string script = Scripts.CreateA
            .Replace("{ZONE}", PowerShellRunner.Quote(request.Zone), StringComparison.Ordinal)
            .Replace("{NAME}", PowerShellRunner.Quote(request.HostLabel), StringComparison.Ordinal)
            .Replace("{IP}", PowerShellRunner.Quote(ip), StringComparison.Ordinal)
            .Replace("{TTL}", request.Ttl.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        if (ptrNode is not null)
        {
            script += PtrScript(request.ReverseZone!, ptrNode, request.Fqdn, request.Ttl, age: false);
        }

        var result = await _runner.RunAsync(Wrap(script, server), null, token).ConfigureAwait(false);
        bool aCreated = result.Output.Contains("CREATED A");

        if (result.ExitCode != 0)
        {
            var error = Translate(result, server);
            // Ist der A schon angelegt und nur der PTR gescheitert, muss das in
            // der Meldung stehen — sonst legt der Nutzer den Host ein zweites Mal an.
            throw aCreated
                ? new DnsAccessException($"Der A-Eintrag wurde angelegt, der PTR nicht: {error.Message}")
                : error;
        }

        var created = new List<DnsRecord> { new(request.Zone, request.Fqdn, RecordType.A, ip, request.Ttl, null) };
        Log.Info("A-Eintrag angelegt: {0} → {1} (Zone {2}, TTL {3}).", request.Fqdn, ip, request.Zone, request.Ttl);

        if (ptrNode is not null)
        {
            string ptrOwner = DnsRecordMapper.OwnerFqdn(ptrNode, request.ReverseZone!);
            created.Add(new DnsRecord(request.ReverseZone!, ptrOwner, RecordType.PTR, request.Fqdn, request.Ttl, null));
            Log.Info("PTR-Eintrag angelegt: {0} → {1}.", ptrOwner, request.Fqdn);
        }

        return created;
    }

    public async Task<DnsRecord> CreatePtrAsync(DnsRecord host, string reverseZone, CancellationToken token)
    {
        if (host.Type != RecordType.A || !Ipv4.TryToUInt32(host.Address, out uint ip))
        {
            throw new ArgumentException($"{host.OwnerName} ist kein A-Eintrag mit IPv4-Adresse.", nameof(host));
        }

        string server = Server;
        string node = DnsRecordMapper.NodeName(ReverseZone.PtrOwnerName(ip), reverseZone);
        bool age = !host.IsStatic;
        string script = PtrScript(reverseZone, node, host.OwnerName, host.Ttl, age);

        var result = await _runner.RunAsync(Wrap(script, server), null, token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw Translate(result, server);

        string owner = DnsRecordMapper.OwnerFqdn(node, reverseZone);
        Log.Info("PTR-Eintrag ergänzt: {0} → {1} (Zone {2}, TTL {3}, {4}).",
            owner, host.OwnerName, reverseZone, host.Ttl, age ? "altert mit" : "statisch");
        return new DnsRecord(reverseZone, owner, RecordType.PTR, host.OwnerName, host.Ttl, age ? DateTime.UtcNow : null);
    }

    private static string PtrScript(string reverseZone, string node, string targetFqdn, uint ttl, bool age) =>
        Scripts.CreatePtr
            .Replace("{RZONE}", PowerShellRunner.Quote(reverseZone), StringComparison.Ordinal)
            .Replace("{NODE}", PowerShellRunner.Quote(node), StringComparison.Ordinal)
            .Replace("{FQDN}", PowerShellRunner.Quote(targetFqdn.TrimEnd('.') + "."), StringComparison.Ordinal)
            .Replace("{TTL}", ttl.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{AGE}", age ? " -AgeRecord" : string.Empty, StringComparison.Ordinal);

    public async Task DeleteRecordAsync(DnsRecord record, CancellationToken token)
    {
        string server = Server;
        string script = Scripts.Delete
            .Replace("{ZONE}", PowerShellRunner.Quote(record.Zone), StringComparison.Ordinal)
            .Replace("{NAME}", PowerShellRunner.Quote(DnsRecordMapper.NodeName(record.OwnerName, record.Zone)), StringComparison.Ordinal)
            .Replace("{TYPE}", PowerShellRunner.Quote(record.Type.ToString()), StringComparison.Ordinal)
            .Replace("{DATA}", PowerShellRunner.Quote(record.Data), StringComparison.Ordinal)
            .Replace("{DATAEXPR}", Scripts.DataExpression(record.Type), StringComparison.Ordinal);

        var result = await _runner.RunAsync(Wrap(script, server), null, token).ConfigureAwait(false);

        if (result.ExitCode == ExitNotFound)
        {
            throw new DnsAccessException($"{record.OwnerName} ({record.Type} {record.Data}) existiert auf dem Server nicht mehr.");
        }
        if (result.ExitCode != 0) throw Translate(result, server);

        Log.Info("Gelöscht: {0} {1} {2} (Zone {3}).", record.OwnerName, record.Type, record.Data, record.Zone);
    }

    public void Invalidate()
    {
        _info = null;
        Log.Debug("Verbindungsstatus verworfen.");
    }

    private async Task<PowerShellResult> RunAsync(string body, string server, Action<string>? onLine, CancellationToken token)
    {
        var result = await _runner.RunAsync(Wrap(body, server), onLine, token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw Translate(result, server);
        return result;
    }

    /// <summary>Rahmen um jedes Skript: Server-Variable, Modulprüfung, Fehler → Exitcode.</summary>
    internal static string Wrap(string body, string server) =>
        Scripts.Frame
            .Replace("{SERVER}", PowerShellRunner.Quote(server), StringComparison.Ordinal)
            .Replace("{BODY}", body, StringComparison.Ordinal);

    /// <summary>Übersetzt eine Fehlerausgabe in eine verständliche Meldung.</summary>
    internal static DnsAccessException Translate(PowerShellResult result, string server)
    {
        string message = string.IsNullOrWhiteSpace(result.Error) ? "keine Fehlermeldung" : result.Error.Trim();
        Log.Error("PowerShell-Zugriff auf {0} fehlgeschlagen (Exitcode {1}): {2}", server, result.ExitCode, message);

        if (result.ExitCode == ExitModuleMissing)
        {
            return new DnsAccessException(
                "Das PowerShell-Modul DnsServer fehlt auf diesem Rechner. Es gehört zu den RSAT-Tools für DNS (dieselben wie die DNS-Konsole).");
        }

        bool Has(params string[] parts) => parts.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));

        if (Has("Zugriff verweigert", "Access denied", "Access is denied", "WIN32 5"))
        {
            return new DnsAccessException($"Zugriff auf den DNS-Server {server} verweigert. Das Konto braucht die Rechte, die es auch in der DNS-Konsole hat.");
        }
        if (Has("RPC", "nicht verfügbar", "unavailable", "WinRM", "konnte keine Verbindung", "cannot connect"))
        {
            return new DnsAccessException($"{server} ist nicht erreichbar: {FirstLine(message)}");
        }
        if (Has("already exists", "bereits vorhanden", "ist bereits"))
        {
            return new DnsAccessException("Der Eintrag existiert bereits.");
        }

        return new DnsAccessException($"Der DNS-Server meldet: {FirstLine(message)}");
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;

    /// <summary>
    /// Die Skripte. Regeln: keine doppelten Anführungszeichen (siehe
    /// <see cref="PowerShellRunner"/>), Ausgabe nur über <c>[Console]::Out.WriteLine</c>
    /// — die Pipeline-Ausgabe von powershell.exe bricht lange Zeilen bei
    /// umgeleiteter Ausgabe sonst unter Umständen an der Fensterbreite um.
    /// </summary>
    internal static class Scripts
    {
        public const string Frame = """
            $ErrorActionPreference = 'Stop'
            $s = {SERVER}
            $T = [char]9
            function W($x) { [Console]::Out.WriteLine($x) }
            if (-not (Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue)) { [Console]::Error.WriteLine('Modul DnsServer fehlt'); exit 2 }
            try {
            {BODY}
            } catch {
              [Console]::Error.WriteLine($_.Exception.Message)
              exit 1
            }
            exit 0
            """;

        public const string Connect = """
            $z = @(Get-DnsServerZone -ComputerName $s)
            W ('ZONEN ' + $z.Count)
            """;

        public const string Load = """
            $zones = @(Get-DnsServerZone -ComputerName $s)
            W ('N' + $T + $zones.Count)
            foreach ($z in $zones) {
              W ('Z' + $T + $z.ZoneName + $T + $z.ZoneType + $T + $z.IsReverseLookupZone + $T + $z.IsDsIntegrated + $T + $z.IsAutoCreated)
            }
            foreach ($z in $zones) {
              if ($z.IsAutoCreated -or $z.ZoneType -eq 'Forwarder' -or $z.ZoneName -eq 'TrustAnchors') { W ('P' + $T + $z.ZoneName); continue }
              try {
                Get-DnsServerResourceRecord -ComputerName $s -ZoneName $z.ZoneName | ForEach-Object {
                  $r = $_
                  $d = $null
                  switch ($r.RecordType) {
                    'A' { $d = $r.RecordData.IPv4Address.IPAddressToString }
                    'AAAA' { $d = $r.RecordData.IPv6Address.IPAddressToString }
                    'CNAME' { $d = $r.RecordData.HostNameAlias }
                    'PTR' { $d = $r.RecordData.PtrDomainName }
                  }
                  if ($d) {
                    $ts = ''
                    if ($r.Timestamp -and $r.Timestamp.Year -gt 1601) { $ts = $r.Timestamp.ToUniversalTime().ToString('o') }
                    W ('R' + $T + $z.ZoneName + $T + $r.HostName + $T + $r.RecordType + $T + [int]$r.TimeToLive.TotalSeconds + $T + $ts + $T + $d)
                  }
                }
              } catch {
                W ('E' + $T + $z.ZoneName + $T + $_.Exception.Message)
              }
              W ('P' + $T + $z.ZoneName)
            }
            """;

        public const string CreateA = """
            Add-DnsServerResourceRecordA -ComputerName $s -ZoneName {ZONE} -Name {NAME} -IPv4Address {IP} -TimeToLive ([TimeSpan]::FromSeconds({TTL}))
            W 'CREATED A'

            """;

        public const string CreatePtr = """
            Add-DnsServerResourceRecordPtr -ComputerName $s -ZoneName {RZONE} -Name {NODE} -PtrDomainName {FQDN} -TimeToLive ([TimeSpan]::FromSeconds({TTL})){AGE}
            W 'CREATED PTR'

            """;

        public const string Delete = """
            $all = @()
            try {
              $all = @(Get-DnsServerResourceRecord -ComputerName $s -ZoneName {ZONE} -Name {NAME} -RRType {TYPE})
            } catch {
              if ($_.CategoryInfo.Category -ne 'ObjectNotFound') { throw }
            }
            $hit = @($all | Where-Object { {DATAEXPR} -eq {DATA} })
            if ($hit.Count -eq 0) { [Console]::Error.WriteLine('nicht gefunden'); exit 3 }
            $hit | Remove-DnsServerResourceRecord -ComputerName $s -ZoneName {ZONE} -Force
            W ('DELETED ' + $hit.Count)
            """;

        /// <summary>Wie der Dateninhalt eines Eintrags in PowerShell gelesen wird — für den Abgleich vor dem Löschen.</summary>
        public static string DataExpression(RecordType type) => type switch
        {
            RecordType.A => "$_.RecordData.IPv4Address.IPAddressToString",
            RecordType.AAAA => "$_.RecordData.IPv6Address.IPAddressToString",
            RecordType.CNAME => "$_.RecordData.HostNameAlias.TrimEnd('.')",
            RecordType.PTR => "$_.RecordData.PtrDomainName.TrimEnd('.')",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }
}
