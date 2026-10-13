using System.Runtime.Versioning;

namespace DnsPilot.Services;

/// <summary>
/// Liefert das Kennwort des Admin-Kontos: aus dem Speicher dieser Sitzung,
/// sonst DPAPI-geschützt aus den Einstellungen.
///
/// Ein Admin-Kennwort dauerhaft abzulegen ist eine Abwägung, die der Nutzer
/// treffen soll — deshalb kann es auch nur für die Sitzung im Speicher stehen
/// und ist nach dem Beenden weg.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialStore(SettingsService settings)
{
    private string? _sessionPassword;

    /// <summary>Kennwort nur für diese Sitzung setzen (null = vergessen).</summary>
    public void SetSessionPassword(string? password) =>
        _sessionPassword = string.IsNullOrEmpty(password) ? null : password;

    /// <summary>Gibt es für das eingestellte Konto ein Kennwort?</summary>
    public bool HasPassword => GetPassword() is not null;

    /// <summary>Das Kennwort, oder null wenn keines bekannt ist.</summary>
    public string? GetPassword() =>
        _sessionPassword ?? SecretProtection.Unprotect(settings.Current.AdminPasswordProtected);
}
