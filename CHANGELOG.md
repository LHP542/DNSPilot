# Versionshinweise

Jede Version bekommt hier einen Abschnitt `## X.Y.Z — Datum`, **bevor** der Tag
gesetzt wird. Der Release-Workflow schneidet ihn als `DnsPilot-X.Y.Z-notes.md`
heraus; DNS-Pilot zeigt ihn im Update-Dialog und unter „Was ist neu“. Ohne
Abschnitt bricht das Release ab.

Geschrieben für die Nutzer: was ist neu, was ist behoben — keine Interna.
Erlaubt sind Überschriften (`###`), Absätze, Aufzählungen, `**fett**`,
`Code` und Hinweise mit `>`.

## 0.2.0 — 2026-10-13

### Updates mit Versionshinweisen

- **DNS-Pilot sucht jetzt alle 30 Minuten nach Updates**, nicht nur beim
  Start. Wird eine neue Version gefunden, erscheint oben ein Hinweis — ohne
  dass ein Fenster die Arbeit unterbricht.
- Der **Update-Dialog zeigt, was sich geändert hat**. Wer mehrere Versionen
  überspringt, sieht die Änderungen aller dazwischenliegenden Versionen.
- Eine Version lässt sich mit **„Diese Version überspringen“** ablehnen; dann
  meldet DNS-Pilot sich erst bei der nächsten wieder.
- **Was ist neu** im Über-Fenster (ⓘ) zeigt die Versionshinweise jederzeit,
  auch ohne Netzlaufwerk. Nach einem Update erscheinen sie einmal von selbst.

> Updates kommen weiterhin aus dem Netzwerkordner
> `\\samba01\542$\5424_IT-Basis-Dienste\DnsPilot`.

## 0.1.1 — 2026-10-13

- DNS-Pilot **merkt sich** Fenstergröße und -position, die Breite der
  Detailspalte, selbst verbreiterte Tabellenspalten und die Filter.
- **Weitere Namen** für denselben Server gelten nicht mehr als Fehler: Zeigt
  der PTR einer Adresse auf einen ihrer Namen, sind die anderen nur ein Hinweis.
- Das Update-Paket ist kleiner (rund 45 statt 73 MB).

## 0.1.0 — 2026-10-13

- Erste Version: alle DNS-Zonen der Domäne in einer Tabelle, mit Suche nach
  Name, Adresse, Netz (`172.16.1.0/24`) und Adressbereich.
- **Auffälligkeiten** ohne Ping: fehlende, verwaiste und widersprüchliche PTRs,
  fehlende A-Einträge, CNAMEs ins Leere, doppelte Adressen, veraltete und
  verdeckte Einträge.
- **Erreichbarkeit prüfen** mit Ping und Namensauflösung, mit Prüfhistorie und
  Aufräum-Kandidaten nach 30 Tagen ohne Antwort.
- Hosts anlegen (A und PTR in einem Schritt) und Einträge löschen — mit
  Bestätigung und Sicherung jedes gelöschten Eintrags.
- Filter „Nur Auffälligkeiten“ und „Nur statische“.
