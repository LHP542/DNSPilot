using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NLog;
// NLog 6: das Attribut liegt in NLog.LayoutRenderers, die Basisklasse aber in
// NLog.LayoutRenderers.Wrappers. Nur eines der beiden zu importieren gibt CS0246
// auf einen Typ, den es sehr wohl gibt.
using NLog.LayoutRenderers;
using NLog.LayoutRenderers.Wrappers;

namespace DnsPilot.Logging;

/// <summary>
/// NLog-Layout-Renderer <c>${masked:inner=…}</c>, der Geheimnisse aus dem Log
/// hält — Passwörter, Tokens und die DPAPI-Blobs aus der Konfiguration.
///
/// DnsPilot loggt bewusst ausführlich, damit sich eine seltsame Auskunft im
/// Nachhinein nachvollziehen lässt. Genau das macht die Maskierung nötig: je
/// mehr im Log steht, desto wahrscheinlicher rutscht irgendwann ein Geheimnis
/// mit hinein.
///
/// <b>Registrierung per ModuleInitializer, nicht in Program.Main:</b> ein Aufruf
/// in Main deckt nur den App-Prozess ab. Der <b>Testprozess hat kein Main</b>,
/// und ohne registrierten Renderer schluckt NLog das <c>${masked:…}</c> samt
/// Nachrichtentext — im Log stünde dann Level, Logger und Exception, aber keine
/// Meldung.
/// </summary>
[LayoutRenderer("masked")]
internal sealed partial class MaskingLayoutRenderer : WrapperLayoutRendererBase
{
    [ModuleInitializer]
    internal static void Register() =>
        LogManager.Setup().SetupExtensions(s =>
            s.RegisterLayoutRenderer<MaskingLayoutRenderer>("masked"));

    protected override string Transform(string text) => Mask(text);

    /// <summary>Ersetzt erkannte Geheimnisse durch <c>***</c>.</summary>
    internal static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string result = KeyValuePattern().Replace(text, m => m.Groups["key"].Value + "***");
        result = ProtectedBlobPattern().Replace(result, "ENC1:***");
        return result;
    }

    /// <summary>
    /// <c>password=…</c>, <c>pwd: …</c>, <c>token=…</c> und Verwandte — bis zum
    /// nächsten Trennzeichen. Bewusst nicht gierig bis zum Zeilenende, sonst
    /// verschwindet der halbe Log-Satz hinter Sternchen und die Meldung wird
    /// unbrauchbar.
    /// </summary>
    [GeneratedRegex(
        @"(?<key>\b(?:password|passwort|pwd|kennwort|secret|token|apikey|api_key)\b\s*[=:]\s*)(?<value>[^\s;,&""']+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValuePattern();

    /// <summary>Die Base64-Blobs aus <see cref="Services.SecretProtection"/>.</summary>
    [GeneratedRegex(@"ENC1:[A-Za-z0-9+/=]+")]
    private static partial Regex ProtectedBlobPattern();

    /// <summary>
    /// Kürzt ein Geheimnis für Diagnosezwecke auf „vorhanden/leer" — nützlich,
    /// wenn im Log stehen soll, <b>ob</b> ein Passwort gesetzt war, ohne es zu
    /// nennen.
    /// </summary>
    internal static string Describe(string? secret) =>
        string.IsNullOrEmpty(secret) ? "(leer)" : $"(gesetzt, {secret.Length} Zeichen)";
}
