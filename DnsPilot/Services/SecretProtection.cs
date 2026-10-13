using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NLog;

namespace DnsPilot.Services;

/// <summary>
/// Schützt einzelne Werte (hier: das Passwort des abweichenden Admin-Kontos)
/// per DPAPI, gebunden an den angemeldeten Windows-Benutzer.
///
/// <b>Warum feldweise und nicht die ganze Datei:</b> Verhaltensbasierte
/// Virenscanner stufen entropiereiche Blobs, die eine Anwendung regelmäßig neu
/// schreibt, als Ransomware-Verhalten ein. Ein verschlüsseltes Einzelfeld in
/// einer sonst lesbaren JSON-Datei fällt nicht auf — eine komplett opake Datei
/// schon. Nebenbei bleibt die Konfiguration für einen Menschen lesbar und
/// notfalls von Hand reparierbar.
///
/// <b>Grenze der Zusicherung:</b> DPAPI schützt gegen Mitlesen durch andere
/// Benutzer desselben Rechners, nicht gegen den Benutzer selbst. Wer das Konto
/// besitzt, kann den Wert entschlüsseln. Für ein Admin-Konto im eigenen AD ist
/// das die angemessene Schutzstufe.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SecretProtection
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Präfix, an dem ein geschützter Wert erkennbar ist.</summary>
    public const string Prefix = "ENC1:";

    /// <summary>
    /// Zusatzentropie: bindet den Geheimtext an diese Anwendung. Ein aus der
    /// Konfiguration kopierter Wert lässt sich damit nicht in einem beliebigen
    /// anderen Programm desselben Benutzers entschlüsseln.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DnsPilot/AdminCredential/v1");

    /// <summary>Verschlüsselt einen Klartextwert. Leereingabe bleibt null.</summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;

        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (CryptographicException ex)
        {
            Log.Error(ex, "Passwort konnte nicht geschützt werden — es wird NICHT gespeichert.");
            return null;
        }
    }

    /// <summary>
    /// Entschlüsselt einen zuvor geschützten Wert. Liefert <c>null</c>, wenn der
    /// Wert fehlt, kein Präfix trägt oder sich nicht entschlüsseln lässt (etwa
    /// weil die Konfiguration von einem anderen Benutzer stammt).
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            Log.Warn("Gespeichertes Passwort trägt kein {0}-Präfix und wird ignoriert.", Prefix);
            return null;
        }

        try
        {
            byte[] cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn(ex, "Gespeichertes Passwort konnte nicht entschlüsselt werden — bitte neu eingeben.");
            return null;
        }
    }

    /// <summary>Trägt der Wert das Präfix eines geschützten Geheimnisses?</summary>
    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);
}
