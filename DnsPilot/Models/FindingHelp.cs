namespace DnsPilot.Models;

/// <summary>
/// Erklärung zu einem Befund für die Detailansicht: was er bedeutet, was er
/// bewirkt und was zu tun ist. Der Befundtext selbst nennt nur den Fall
/// („PTR fehlt in …"); wer nicht täglich mit DNS arbeitet, braucht den
/// Zusammenhang dazu.
/// </summary>
/// <param name="Title">Kurzname des Befunds.</param>
/// <param name="Meaning">Was der Befund bedeutet.</param>
/// <param name="Impact">Welche Auswirkung er hat — und warum er so gewichtet ist.</param>
/// <param name="Remedy">Was zu tun ist.</param>
public sealed record FindingHelp(string Title, string Meaning, string Impact, string Remedy)
{
    /// <summary>Grundbegriff, auf den sich mehrere Erklärungen stützen.</summary>
    internal const string PtrBasics =
        "Ein A-Eintrag übersetzt Name → Adresse. Ein PTR (Pointer) ist die Gegenrichtung, Adresse → Name, "
        + "die „Rückwärtsauflösung“. Er liegt nicht in der Namenszone, sondern in einer eigenen Reverse-Zone "
        + "(z. B. 1.16.172.in-addr.arpa für 172.16.1.x).";

    public static FindingHelp For(FindingKind kind, Severity severity) => kind switch
    {
        FindingKind.MissingPtr => new(
            "PTR fehlt",
            PtrBasics + " Für diese Adresse gibt es eine Reverse-Zone, aber keinen PTR — die Adresse lässt sich "
            + "nicht in einen Namen zurückübersetzen.",
            "Überall, wo nur die Adresse ankommt, bleibt sie namenlos: Firewall-, Proxy- und Server-Logs, "
            + "„nslookup <Adresse>“, Monitoring und Netzwerk-Werkzeuge zeigen nur die Zahl. Dienste, die die "
            + "Gegenstelle rückwärts auflösen (SSH, Mailserver, manche Anmelde- und Lizenzverfahren), werden "
            + "langsam oder lehnen ab. Warnung, weil der Eintrag fast immer schlicht vergessen wurde — typisch bei "
            + "von Hand angelegten statischen Hosts.",
            "PTR anlegen (Schreibmodus, Knopf „PTR anlegen“). Vorher sicherstellen, dass dieser Name der richtige "
            + "Name für die Adresse ist. Bei einem dynamischen Eintrag sollte eigentlich der Client oder der "
            + "DHCP-Server den PTR registrieren — dort lohnt ein Blick in die Einstellungen."),

        FindingKind.NoReverseZone => new(
            "Keine Reverse-Zone",
            PtrBasics + " Für das Netz dieser Adresse gibt es auf dem Server keine Reverse-Zone, also kann es "
            + "auch keinen PTR geben.",
            "Wie bei einem fehlenden PTR: die Adresse bleibt in Logs und Werkzeugen namenlos. Nur eine Info, weil "
            + "das oft gewollt ist (Gäste-WLAN, Transfernetze, fremde Netze).",
            "Nur handeln, wenn das Netz zur eigenen Infrastruktur gehört: dann eine Reverse-Zone für das Netz "
            + "anlegen (DNS-Konsole) und die PTR nachziehen."),

        FindingKind.PtrMismatch when severity == Severity.Info => new(
            "Adresswechsel",
            PtrBasics + " Vorwärts und rückwärts ergeben verschiedene Namen bzw. Adressen. Alle beteiligten "
            + "Einträge sind dynamisch.",
            "Typisch für ein Gerät, das das Netz gewechselt hat (Notebook: LAN, WLAN, VPN). Der alte Eintrag "
            + "verschwindet von selbst, wenn der Server dynamische Einträge altern lässt und aufräumt "
            + "(Scavenging). Deshalb nur eine Info.",
            "Nichts tun. Bleibt der Befund über Wochen stehen, prüfen, ob das Aufräumen auf dem Server läuft."),

        FindingKind.PtrMismatch => new(
            "PTR und A widersprechen sich",
            PtrBasics + " Hier passen beide Richtungen nicht zusammen: der Name zeigt auf diese Adresse, die "
            + "Adresse aber auf einen anderen Namen (oder umgekehrt).",
            "Logs und Monitoring zeigen für die Adresse den anderen — oft alten — Namen, die Fehlersuche führt "
            + "zum falschen Gerät. Dienste, die vorwärts und rückwärts gegenprüfen (SSH, Mail, manche "
            + "Kerberos-Konfigurationen), melden Fehler oder Warnungen. Warnung, weil mindestens ein Eintrag "
            + "statisch ist: das Aufräumen des Servers löscht nur dynamische Einträge, der Widerspruch bleibt "
            + "also stehen, bis ihn jemand von Hand behebt. Wirkt der Name im PTR auf 15 Zeichen gekürzt, hat "
            + "ihn oft ein Gerät mit seinem NetBIOS-Namen registriert.",
            "Klären, welcher Name für die Adresse richtig ist. Den falschen PTR löschen und für den richtigen "
            + "Namen einen PTR anlegen; ist stattdessen der A-Eintrag veraltet, diesen löschen."),

        FindingKind.PtrOrphan => new(
            "Verwaister PTR",
            PtrBasics + " Dieser PTR zeigt auf einen Namen, zu dem es keinen A-Eintrag mehr gibt, und er wird "
            + "nicht (mehr) erneuert.",
            "Die Adresse wird rückwärts zu einem Gerät aufgelöst, das es nicht mehr gibt. Wird die Adresse neu "
            + "vergeben, zeigen Logs und Werkzeuge den alten Namen — eine falsche Spur. Ein statischer PTR "
            + "verschwindet nie von selbst.",
            "Löschen, wenn die Adresse frei oder neu vergeben ist. Die Prüfung (Ping) zeigt, ob unter der "
            + "Adresse noch etwas antwortet."),

        FindingKind.MissingA => new(
            "A-Eintrag fehlt",
            "Der PTR wird aktiv registriert (frischer Zeitstempel), zum Namen gibt es aber keinen A-Eintrag. "
            + "Das Gerät lebt also — nur die Vorwärtsrichtung kommt nicht zustande.",
            "Der Name lässt sich nicht auflösen: wer das Gerät per Name anspricht, erreicht es nicht. Häufige "
            + "Ursache: ein Gerät außerhalb der Domäne darf in einer Zone mit „nur sicheren Updates“ keinen "
            + "A-Eintrag schreiben, der DHCP-Server legt aber den PTR an.",
            "Nicht den PTR löschen — er käme beim nächsten Lease wieder. Klären, wer den A-Eintrag registrieren "
            + "soll (DHCP-Server mit eigenen Anmeldedaten, Gerät in die Domäne, oder statisch anlegen)."),

        FindingKind.PtrExternal => new(
            "PTR-Ziel extern",
            "Der PTR zeigt auf einen Namen in einer Domäne, die dieser Server nicht verwaltet.",
            "Ob das Ziel existiert, lässt sich hier nicht beurteilen. Nur eine Info.",
            "Nur prüfen, wenn der Name unbekannt vorkommt — etwa ein Überrest aus einer alten Domäne."),

        FindingKind.CnameDangling => new(
            "CNAME ins Leere",
            "Ein CNAME ist ein Alias: der Name verweist auf einen anderen Namen. Das Ziel dieses Alias gibt es "
            + "in den eigenen Zonen nicht.",
            "Wer den Alias benutzt, erhält „Name nicht gefunden“. Meist ist der Zielhost abgebaut oder "
            + "umbenannt worden, der Alias blieb übrig.",
            "Alias löschen, wenn er nicht mehr gebraucht wird, sonst auf den neuen Host umbiegen. Vorher klären, "
            + "ob noch Verknüpfungen, Skripte oder Konfigurationen den Alias verwenden."),

        FindingKind.DuplicateIp => new(
            "Doppelte Adresse",
            "Mehrere Namen zeigen auf dieselbe Adresse, und kein PTR bestätigt einen davon als Hauptnamen.",
            "Es ist unklar, welcher Name gilt — oft ist einer davon ein altes Gerät, dessen Adresse neu vergeben "
            + "wurde. Wer den alten Namen anspricht, landet beim falschen Gerät.",
            "Herausfinden, welches Gerät die Adresse heute hat (Prüfung, Anmeldung am Gerät), den veralteten "
            + "Namen löschen und für den gültigen einen PTR anlegen."),

        FindingKind.AliasName => new(
            "Weiterer Name",
            "Die Adresse hat mehrere Namen, und der PTR nennt den Hauptnamen. DNS-seitig ist das stimmig.",
            "Keine technische Störung. Ob der zusätzliche Name gewollt ist (Dienstname, Alias) oder ein alter "
            + "Name, kann nur ein Mensch entscheiden.",
            "Nur löschen, wenn der Name nicht mehr gebraucht wird."),

        FindingKind.Stale => new(
            "Veraltet",
            "Ein dynamischer Eintrag, den der Client lange nicht erneuert hat. Geräte erneuern ihre Einträge "
            + "regelmäßig, solange sie im Netz sind.",
            "Das Gerät ist vermutlich weg oder hat einen anderen Namen. Läuft auf dem Server das Aufräumen "
            + "(Scavenging), verschwindet der Eintrag von selbst.",
            "Abwarten, wenn Scavenging läuft. Sonst prüfen und ggf. löschen."),

        FindingKind.Shadowed => new(
            "Verdeckt",
            "Der Eintrag liegt in einer Zone, für deren Namen es eine speziellere Unterzone gibt. Clients fragen "
            + "immer die speziellste Zone.",
            "Dieser Eintrag wird nie ausgeliefert — er ist wirkungslos, kann aber bei der Fehlersuche in die Irre "
            + "führen.",
            "Prüfen, ob es den Eintrag in der Unterzone gibt; wenn ja, diesen hier löschen, sonst dort anlegen."),

        _ => new(kind.ToString(), string.Empty, string.Empty, string.Empty),
    };
}
