# DNS-Pilot

DNS-Einträge prüfen, durchsuchen und aufräumen — für die AD-integrierten Zonen
auf den Domänencontrollern.

Die DNS-Verwaltungskonsole kann anlegen und löschen, sagt aber nicht, ob ein
Host noch existiert, ob A- und PTR-Eintrag zusammenpassen oder was alles auf
eine Adresse verweist. Genau das zeigt DNS-Pilot.

![Hauptfenster](docs/hauptfenster.png)

## Was DNS-Pilot kann

- **Alle Zonen auf einen Blick** — A, AAAA, CNAME und PTR aus allen Forward-
  und Reverse-Zonen in einer sortierbaren Tabelle.
- **Auffälligkeiten finden**, ohne etwas anzupingen:
  - PTR fehlt, obwohl es eine Reverse-Zone gibt
  - PTR zeigt auf einen Host, den es nicht mehr gibt (**verwaist** — der PTR
    ist statisch oder wird nicht mehr erneuert)
  - PTR wird aktiv registriert, aber der **A-Eintrag fehlt** — hier ist nicht
    der PTR das Problem; typisch bei Geräten außerhalb der Domäne in einer
    Zone, die nur sichere Updates zulässt
  - PTR und A widersprechen sich
  - CNAME zeigt ins Leere
  - eine Adresse hängt an mehreren Namen
  - dynamischer Eintrag hat sich seit Wochen nicht mehr registriert
  - Eintrag ist **verdeckt**: er liegt in einer Zone, für die es inzwischen
    eine speziellere Unterzone gibt — Clients sehen ihn nie
- **Erreichbarkeit prüfen** — Ping, optional TCP-Ports, dazu ein Vergleich, ob
  der Name tatsächlich auf die eingetragene Adresse auflöst.
- **„Zuletzt online" merken** — jede Prüfung wird festgehalten. Was über
  Wochen bei mehreren Prüfungen nie geantwortet hat, wird als
  **Aufräum-Kandidat** markiert.
- **Nach Adressen und Netzen suchen** — „verweist noch irgendwas auf
  172.16.1.11?" oder „was liegt in 172.16.1.0/24, und was ist dort frei?"
- **Hosts anlegen** — A und PTR in einem Schritt, mit Warnung bei bereits
  vergebener Adresse.
- **Einträge löschen** — mit Bestätigungsliste, passendem PTR als Vorschlag
  und einer Sicherung jedes gelöschten Eintrags.

## Installation

1. Das ZIP `DnsPilot-X.Y.Z-win-x64.zip` aus dem Netzwerkordner
   `\\samba01\542$\5424_IT-Basis-Dienste\DnsPilot` in einen eigenen Ordner
   entpacken (z. B. `C:\Tools\DnsPilot`).
2. `DnsPilot.exe` starten. Eine .NET-Installation ist nicht nötig.

Updates bietet DNS-Pilot beim Start an; installiert werden sie im
Über-Fenster (ⓘ) mit **⬇ Update installieren**.

## Anmeldung

DNS-Pilot greift über das PowerShell-Modul `DnsServer` auf den DNS-Server zu
(`Get-DnsServerResourceRecord` und Verwandte). Das Modul gehört zu den
RSAT-Tools für DNS — wo die DNS-Konsole installiert ist, ist es in der Regel
auch da. Lesen darf, wer die Zonen sehen darf; anlegen und löschen nur, wer
auch ändern darf (DnsAdmins).

Auf dem Rechner muss kein .NET installiert sein, das Programm bringt seine
Laufzeit mit.

Zwei Wege:

- **Auf dem Jumphost** oder per Rechtsklick → *Als anderer Benutzer ausführen*
  unter dem Admin-Konto starten. Dann bleibt das Feld *Admin-Konto* in den
  Einstellungen leer.
- **Vom eigenen Rechner unter dem Alltagskonto:** in den Einstellungen (⚙) das
  Admin-Konto (`LHP\adm.name`) und das Kennwort eintragen. Das Kennwort gilt
  dann für die laufende Sitzung; mit *Kennwort merken* wird es
  DPAPI-verschlüsselt gespeichert und lässt sich nur unter deinem
  Windows-Konto auf diesem Rechner wieder entschlüsseln.

Voreingestellt ist der Server **DC03**; auf dem Jumphost hat sich **DC01** bewährt. Die Zonen sind auf allen DCs gleich;
ist einer nicht erreichbar, einfach DC01 oder DC02 eintragen.

## Bedienung

### Suchen

Das Suchfeld versteht:

| Eingabe | Ergebnis |
|---|---|
| `drucker` | alle Einträge, in denen „drucker" vorkommt |
| `172.16.1.11` | alles, was auf diese Adresse verweist — auch verwaiste PTRs |
| `172.16.1.0/24` | alles im Netz, dazu eine Übersicht mit freien Adressen |
| `172.16.1.10-50` | ein Adressbereich |
| `172.16.1.*` | Kurzform für ein /24 |

Daneben filtern *Zone*, *Typ*, **Nur Auffälligkeiten** und **Nur statische**.

**Warum „Nur statische“:** Dynamische Einträge (mit Zeitstempel) räumt der
DNS-Server selbst weg, sobald sich ein Gerät lange nicht mehr meldet — in der
Domäne läuft das wöchentlich. Statische Einträge fasst dieses
Aufräumen nie an. Dort liegt der Altbestand, den man von Hand prüfen muss.
Hat ein Notebook das Netz gewechselt, zeigen PTR und A kurzzeitig auf
verschiedene Adressen; sind beide dynamisch, meldet DNS-Pilot das nur als
Hinweis („Adresswechsel“), weil es sich von selbst erledigt.

![Netzsuche](docs/netzsuche.png)

Bei einer Netzsuche kopiert **⧉ Freie Adressen kopieren** alle Adressen ohne
A- oder PTR-Eintrag in die Zwischenablage.

### Die zwei Punkte vorn in jeder Zeile

| Spalte | Grau | Weiß | Orange | Rot | Grün |
|---|---|---|---|---|---|
| **DNS** | in Ordnung | Hinweis | Warnung | Fehler | – |
| **Netz** | ungeprüft | – | zuletzt nicht erreicht | Aufräum-Kandidat | online |

Die Spalte *Hinweise* und die Details rechts sagen, was genau auffällt.

### Prüfen

**Sichtbare prüfen** prüft alle Einträge, die gerade angezeigt werden;
**Auswahl prüfen** nur die markierten Zeilen (Strg/Umschalt + Klick). Bei mehr
als 300 Einträgen fragt die Schaltfläche einmal nach.

Geprüft werden nur Adressen, die im DNS stehen — es gibt bewusst keine
Funktion, die ein ganzes Netz abtastet. Die Prüfung läuft gedrosselt
(Standard: 8 gleichzeitig, 50 ms Pause), damit sie für Endpoint-Schutz und
IDS nicht wie ein Angriff aussieht.

**Aufräum-Kandidat** wird eine Adresse erst, wenn sie seit mindestens 30 Tagen
**und** bei mindestens 3 Prüfungen in Folge nicht geantwortet hat. Es lohnt
sich also, über einige Wochen hinweg immer mal wieder zu prüfen. Auch dann gilt:
Ein Server hinter einer Firewall, die Ping blockt, sieht genauso aus wie ein
verschrotteter Rechner. Im Zweifel die TCP-Prüfung einschalten oder nachfragen.

![Auffälligkeiten](docs/auffaelligkeiten.png)

### Anlegen und Löschen

Ändern ist gesperrt, bis **Änderungen erlauben** angehakt ist. Beim nächsten
Start ist die Sperre wieder an.

**＋ Neuer Host** legt einen statischen A-Eintrag an und, wenn es für das Netz
eine Reverse-Zone gibt, den passenden PTR. Ist die Adresse schon vergeben oder
der Name schon da, muss das ausdrücklich bestätigt werden. Wer vorher nach
einem Netz gesucht hat, bekommt die erste freie Adresse vorgeschlagen.

![Neuer Host](docs/neuer-host.png)

**Löschen …** (oder Entf in der Tabelle) zeigt die markierten Einträge noch
einmal einzeln. Zu einem A-Eintrag wird der PTR, der auf ihn zeigt, mit
vorgeschlagen. Gelöscht wird erst nach dem Haken bei *Ich habe die Liste
geprüft*.

![Löschen](docs/loeschen.png)

Der DNS-Server hat keinen Papierkorb. DNS-Pilot schreibt deshalb **vor** jedem
Löschen Name, Zone, Typ, Daten und TTL in eine Sicherungsdatei. Die Schaltfläche
**Sicherungen** öffnet den Ordner.

### Tastatur

| Taste | Wirkung |
|---|---|
| Strg+F | ins Suchfeld |
| Esc | Suche leeren |
| F5 | neu laden |
| Entf | markierte Einträge löschen (im Schreibmodus) |

## Einstellungen

![Einstellungen](docs/einstellungen.png)

- **DNS-Server** — Name oder Adresse eines DCs.
- **Anmeldung** — optionales Admin-Konto, siehe oben.
- **Prüfung** — Wartezeit pro Ping, Anzahl gleichzeitiger Prüfungen, Pause,
  Namensauflösung, optionale TCP-Ports.
- **Bewertung** — ab wann ein dynamischer Eintrag als veraltet gilt und ab
  wann eine Adresse Aufräum-Kandidat wird; TTL für neue Einträge.
- **Updates** — Netzwerkordner für Updates.

## Wo DNS-Pilot Daten ablegt

Alles unter `%LOCALAPPDATA%\DnsPilot\`:

| Datei/Ordner | Inhalt |
|---|---|
| `settings.json` | Einstellungen (Kennwort nur DPAPI-verschlüsselt) |
| `sightings.json` | Prüfhistorie: Adresse, Zeitpunkte, Zähler — keine Namen |
| `geloescht\JJJJ-MM-TT.jsonl` | Sicherung gelöschter Einträge, eine Zeile pro Eintrag |
| `logs\` | Protokolle (Kennwörter werden maskiert) |

## Entwicklung

Siehe [CLAUDE.md](CLAUDE.md). Bauen und testen:

```bash
dotnet build
dotnet test
```

Die Bildschirmfotos in `docs/` entstehen mit Demodaten aus dem Programm selbst:
`DnsPilot.exe --screenshots docs` (Debug-Build).
