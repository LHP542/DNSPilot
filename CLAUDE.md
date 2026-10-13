# DNS-Pilot

## Grundlagen

- **Was:** Windows-Werkzeug zum Prüfen, Durchsuchen und Aufräumen der
  DNS-Einträge auf den Domänencontrollern (DC01–DC03, AD-integriert). Zeigt, ob
  Hosts erreichbar sind, ob A und PTR zueinander passen, welche Einträge
  verwaist sind, und was alles auf eine Adresse oder ein Netz verweist.
  Anlegen und Löschen nur im ausdrücklich eingeschalteten Schreibmodus.
- **Stack:** C# / .NET 10 (`net10.0-windows`) / Avalonia 12.1 mit DataGrid,
  CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, NLog (mit
  Secret-Masking), xunit.v3 + FluentAssertions 7.x. DNS-Zugriff über das
  PowerShell-Modul `DnsServer` (RSAT) per `powershell.exe`.
- **Struktur:** Flach (kein `src/`), `.slnx`, Central Package Management,
  `Directory.Build.props`, MinVer (Tags `v*`).
- **Repo:** `github.com/LHP542/DNSPilot` (öffentlich; lokaler Ordner `DNS-Pilot`).
  **Öffentlich heißt:** keine echten Hostnamen, Adressen oder Netze in Tests,
  Kommentaren und Doku — Fälle aus dem echten Netz mit erfundenen Namen
  nachbauen. Domäne, DC-Namen und Update-Pfad sind wie bei LDAPeek in Ordnung.
- **Herkunft:** Gerüst aus LDAPeek übernommen (Chrome, Tray, Single-Instance,
  Update-Kanal, Einstellungen, Masking, CI) — gleiche Bausteine, gleiche Fallen.
- **Kommunikation:** Deutsch, „du". Lars entwirft, Claude implementiert.

### Bewusste Abweichungen vom Kroste-Standard

Wie bei LDAPeek, und aus denselben Gründen — nicht ungefragt „nachziehen":

1. **Windows-only.** Der DNS-Zugriff läuft über das PowerShell-Modul
   `DnsServer`, das gibt es nur unter Windows. Ein Linux-Build wäre eine Attrappe.
2. **Nur Deutsch**, keine EN+DE-Lokalisierung. Dienstliches Werkzeug.
3. **Update über einen Netzwerkordner** statt GitHub Releases:
   `\\samba01\542$\5424_IT-Basis-Dienste\DnsPilot`.

Kein BMC-Link, keine `FUNDING.yml`, keine Zugangsgruppe wie in LDAPeek: wer
keine DNS-Rechte hat, sieht ohnehin nichts.

### Umgebungshinweis: nuget.org-Downloads sind geblockt

Siehe LDAPeek: die Versionen in `Directory.Packages.props` entsprechen dem
Offline-Feed `C:\NuGet-Local`, nicht dem Neuesten auf nuget.org. Vor jedem
Bump prüfen: `ls C:/NuGet-Local | grep -i <paket>`.

**Lokal veröffentlichen** (z. B. für den Jumphost, auf dem nur .NET 4.8 liegt —
das Paket bringt die Laufzeit mit):

```bash
dotnet publish DnsPilot/DnsPilot.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableSingleFileAnalyzer=false -o publish/win-x64
```

`EnableSingleFileAnalyzer=false` ist nur lokal nötig: Single-File zieht sonst
für den Analyzer `Microsoft.NET.ILLink.Tasks` in der SDK-Version (10.0.11), und
dessen Download blockt der Proxy (403). Im Release-Workflow bleibt der
Analyzer an. Ausgeliefert werden nur `DnsPilot.exe` und `nlog.config`
(ZIP rund 45 MB); die PDBs daneben sind 100 MB Debug-Ballast.
`Compress-Archive` scheiterte dabei an einer gesperrten `nlog.config` —
`C:\Windows\System32\tar.exe -a -c -f x.zip …` ging.

## Aktueller Stand

**2026-10-13** — 209 Tests grün, Oberfläche per Bildschirmfoto-Modus mit
Demodaten geprüft. Der DNS-Zugriff ist von WMI auf PowerShell umgestellt
(siehe „Warum PowerShell").

**Erster echter Lauf (Jumphost, DC01, 2026-10-13):** 189 Zonen gemeldet, 185
angezeigt, 13.802 Einträge (A/AAAA/CNAME/PTR) in 19,5 s, keine Zone unlesbar.
Die vorgeschaltete Verbindungsprüfung kostete weitere 6,8 s und entfällt beim
Laden jetzt. Danach brach das Laden ab: derselbe PTR stand in
`20.10.in-addr.arpa` und `255.20.10.in-addr.arpa`, und `DnsRecord.Key`
enthielt die Zone nicht. Behoben; daraus ist der Befund „verdeckt" entstanden.
Anlegen und Löschen sind gegen die echten DCs noch nicht gelaufen.

Was gebaut ist:

- **Laden** aller Zonen und der Einträge A, AAAA, CNAME, PTR in einem
  PowerShell-Lauf mit Fortschritt pro Zone. Cache, TrustAnchors und die
  automatischen Reverse-Zonen (0/127/255) werden ausgeblendet; eine nicht
  lesbare Zone wird geloggt und hält den Rest nicht auf.
- **Analyse** (`DnsAnalyzer`, rein auf den geladenen Daten): PTR fehlt,
  keine Reverse-Zone, PTR widerspricht A, PTR verwaist (statisch oder nicht
  mehr erneuert) bzw. A-Eintrag fehlt (PTR frisch registriert — auf dem
  ersten echten Lauf bei Thin Clients gefunden, Zone nur mit sicheren Updates), PTR-Ziel extern,
  CNAME ins Leere, doppelte Adresse, veralteter dynamischer Eintrag,
  verdeckter Eintrag (liegt in einer Zone, deren Unterzone für den Namen
  zuständig ist — Clients sehen ihn nie).
  DC-Einträge (Zonenspitze, DomainDnsZones, ForestDnsZones, gc, `_msdcs`)
  sind von „doppelte Adresse" ausgenommen.
- **Widerspruch PTR/A** ist nur eine Info, wenn alle Beteiligten dynamisch
  sind (Notebook hat das Netz gewechselt), sonst eine Warnung.
- **Scavenging in der Domäne** (geprüft 2026-10-13): läuft auf einem der DCs
  wöchentlich, die Zonen haben Aging an. Dynamische Einträge erledigen sich
  also — der Altbestand sind die **statischen**, dafür der Filter „Nur
  statische". Nachsehen mit `Get-DnsServerScavenging -ComputerName <DC>` für
  jeden DC; ein `ScavengingInterval` von 0 heißt „räumt nicht auf".
- **Suche**: Freitext, Adresse, CIDR, Bereich (`.10-50`), Platzhalter (`.*`).
  Bei Adressen/Netzen eine Netzübersicht mit Anzahl freier Adressen und
  zuständiger Reverse-Zone; freie Adressen kopierbar.
- **Prüfung** (`ScanService`): Ping, optional TCP-Ports nacheinander, Auflösung
  über den Resolver. Gedrosselt (Standard 8 parallel, 50 ms Pause), jede
  Adresse nur einmal pro Lauf, ab 300 Einträgen Rückfrage per zweitem Klick.
- **Prüfhistorie** (`SightingStore`, `%LOCALAPPDATA%\DnsPilot\sightings.json`):
  „zuletzt online", Fehlversuche in Folge. Urteil „Kandidat" erst bei
  Zeit **und** Mindestzahl Fehlversuche (`SightingEvaluator`).
- **Anlegen** von A + PTR mit Live-Prüfung gegen das Inventar
  (`NewHostValidator`); Hinweise wie „Adresse vergeben" müssen bestätigt werden.
- **Löschen** mit Bestätigungsliste, Vorschlag des passenden PTR, Pflicht-
  Häkchen „Liste geprüft" und JSON-Sicherung jedes Eintrags **vor** dem
  Löschen (`DeletionBackup`, `%LOCALAPPDATA%\DnsPilot\geloescht\*.jsonl`).
- **Schreibmodus** pro Sitzung, nicht gespeichert.
- **Admin-Konto** optional mit Kennwort nur im Speicher oder DPAPI-geschützt.
- CSV-Export, Tray, Single-Instance, Über-Fenster mit Self-Update.

## Roadmap

Kandidaten, die beim Bauen aufgefallen sind — nichts davon ist zugesagt:

- **Erster Lauf gegen DC01** (187 Zonen) und dabei messen: Ladezeit gesamt
  steht im Log. Falls das spürbar dauert, die Zonen auf zwei, drei
  PowerShell-Läufe verteilen.
- **Anlegen und Löschen** über die Cmdlets sind nur gegen eine Attrappe
  getestet — der erste echte Lauf am besten mit einem Testeintrag.
- **Wiederherstellen aus der Löschsicherung** — `DeletionBackup.Read` gibt es
  schon, es fehlt nur der Dialog.
- **AD-Computerkonten abgleichen** (`lastLogonTimestamp`) als weiteres Merkmal
  für Kandidaten; die LDAP-Bausteine liegen in LDAPeek.
- **Checkmk-Abgleich**: Ist der Host dort noch überwacht?
- **IPv6-Reverse-Zonen** (`ip6.arpa`) — AAAA wird geladen, aber nicht gegen
  PTR geprüft.
- **Weitere Typen** (SRV, MX, NS) als Verweise — ein Host kann noch als
  SRV-Ziel gebraucht werden, obwohl kein CNAME auf ihn zeigt.
- **Scavenging-Status** der Zonen anzeigen (Aging an/aus, Intervalle).

## Referenz

### Architektur

```
DnsPilot/
  Models/       DnsRecord (+ RecordType, DnsZone, ZoneKind), Finding,
                HostSighting (+ Reachability), DnsServerInfo (+ DnsSnapshot,
                NewHostRequest), AppSettings
  Services/     PowerShellDnsServer (IDnsServer), PowerShellRunner,
                DnsRecordMapper, DnsInventory,
                DnsAnalyzer, SearchQuery, IpRange (+ Ipv4), ReverseZone,
                ScanService, HostProbe (IHostProbe), SightingStore,
                SightingEvaluator, NewHostValidator, DeletionBackup,
                RecordCsv, CredentialStore, DemoDnsServer (+ DemoHostProbe),
                SettingsService, JsonFileStore, SecretProtection,
                UpdateService, UpdateChannel
  ViewModels/   MainWindowViewModel (+ .Checks.cs, .Changes.cs), RecordRow,
                NewHostWindowViewModel, DeleteWindowViewModel,
                SettingsWindowViewModel
  Views/        ChromeWindow, MainWindow, NewHostWindow, DeleteWindow,
                SettingsWindow, AboutWindow, TrayController,
                SingleInstanceGuard, GlobalExceptionHandler, ScreenshotTool
```

Die Logik liegt in `Services/` und ist ohne DNS-Server testbar.
`PowerShellDnsServer` wird mit einer Attrappe für `IPowerShellRunner` getestet
(welches Skript entsteht, wie die Ausgabe gelesen wird), `PowerShellRunnerTests`
läuft gegen das echte `powershell.exe` (Kommandozeile, Exitcode, Umlaute).

### Warum PowerShell (und nicht WMI oder dnscmd)

Erster Ansatz war WMI (`\\server\root\MicrosoftDNS` per DCOM). Auf dem Jumphost
mit dem Admin-Konto: **„Zugriff verweigert"** — obwohl die DNS-Konsole mit
demselben Konto funktioniert. Die Konsole spricht ein eigenes RPC-Protokoll
(MS-DNSP), WMI per DCOM verlangt auf einem DC Admin-Rechte. Lokal sind die DCs
ohnehin gesperrt. Getestet am 2026-10-13:

| Weg | Ergebnis auf dem Jumphost |
|---|---|
| WMI `root\MicrosoftDNS` (DCOM) | Zugriff verweigert |
| `dnscmd DC01 /enumzones` | geht (187 Zonen), Ausgabe lokalisierter Text |
| `Get-DnsServerResourceRecord -ComputerName DC01` | geht, liefert Objekte |

PowerShell gewinnt: Objekte statt deutschem Text, **ein** Prozess für alle
Zonen statt einem `dnscmd` pro Zone. Auf dem Jumphost ist nur .NET 4.8 —
egal, das Paket ist self-contained, und `powershell.exe` 5.1 ist da.

### PowerShell-Fakten

- Start als `powershell.exe -NoProfile -NonInteractive -Command <Skript>`.
  **Kein `-EncodedCommand`, kein `-ExecutionPolicy Bypass`** — beides sind
  klassische Malware-Merkmale, auf die Trend Micro reagiert. Für `-Command`
  gilt die Ausführungsrichtlinie ohnehin nicht.
- **Skripte ohne doppelte Anführungszeichen** (der Runner weist sie ab, ein
  Test prüft alle Vorlagen). Werte kommen per `PowerShellRunner.Quote` in
  einfache Anführungszeichen (`'` → `''`).
- **Ausgabe über `[Console]::Out.WriteLine`**, nicht über die Pipeline —
  sonst kann powershell.exe lange Zeilen bei umgeleiteter Ausgabe umbrechen.
- Das Lese-Skript schreibt tab-getrennte Zeilen (`N`/`Z`/`R`/`E`/`P`, Format in
  `DnsRecordMapper`), kein JSON: `ConvertTo-Json` ist in 5.1 bei zehntausenden
  Einträgen langsam, und die Zeilen erlauben Fortschritt pro Zone.
- `HostName` ist relativ zur Zone (`@` = Spitze), Zielnamen haben einen
  Schlusspunkt, `Timestamp` ist bei statischen Einträgen leer.
- Exitcodes im Rahmen: 0 ok, 1 Fehler (Meldung auf stderr), 2 Modul fehlt,
  3 Eintrag beim Löschen nicht gefunden.
- Löschen: `Get-DnsServerResourceRecord -Name -RRType`, Inhalt in PowerShell
  vergleichen, dann `Remove-DnsServerResourceRecord -Force`. Anlegen:
  `Add-DnsServerResourceRecordA` / `…Ptr`, nach jedem Schritt eine
  `CREATED …`-Zeile, damit ein halb gelungenes Anlegen erkennbar ist.

### Warum die Prüfung so zurückhaltend ist

Lars' Sorge beim Projektstart: was macht Trend Micro, wenn das Werkzeug ein
Netz abpingt? Deshalb: es gibt **keine** Funktion „ganzes Netz scannen",
geprüft wird nur, was im DNS steht; Standard 8 gleichzeitig mit 50 ms Pause;
TCP-Ports standardmäßig aus und dann nacheinander statt parallel. Wer das
lockert, sollte vorher mit dem Endpoint-Schutz-Team sprechen.

### UI-Fallen (Avalonia 12)

- **DataGrid mit kompilierten Bindings:** `x:DataType="vm:RecordRow"` direkt
  an der `DataGridTextColumn` funktioniert. `DataGrid.SelectedItems` ist nicht
  bindbar — die Mehrfachauswahl reicht `MainWindow.axaml.cs` per
  `SelectionChanged` durch.
- **Feste Spaltenbreiten brauchen mehr Luft als erwartet:** „CNAME" brauchte
  96 px, „13.03.2026" 122 px — die Zellen haben deutlich mehr Innenabstand als
  das `Padding` im Style vermuten lässt. Am Bildschirmfoto prüfen, nicht
  schätzen.
- **Sortierung** über `SortMemberPath`: `NameSortKey` (PTR numerisch hinter
  die Hosts), `DataSortKey` (Adressen numerisch), `TimestampSortKey`.
- **`IReadOnlyList.Count` im Binding vermeiden** — dafür gibt es `HasFindings`;
  die Eigenschaft liegt auf dem Basis-Interface.
- Übrige Fallen (WrapPanel statt StackPanel, `--` in XAML-Kommentaren,
  `PlaceholderText`, `GlobalUsings.cs`) wie in LDAPeek; `XamlConsistencyTests`
  fängt tote Klassen, fehlende Ressourcen und `--` in Kommentaren ab.

### Bildschirmfotos

`DnsPilot.exe --screenshots docs` (nur Debug-Build) rendert alle Fenster mit
den Demodaten aus `DemoDnsServer` nach `docs/`. Die Demodaten enthalten je
ein Beispiel für jeden Befund; `DnsAnalyzerTests` sichert das ab.
