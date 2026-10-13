using System.Runtime.CompilerServices;
using FluentAssertions;
using DnsPilot.Logging;
using NLog;
using NLog.Layouts;

namespace DnsPilot.Tests;

/// <summary>
/// Secret-Masking im Log.
///
/// Bewusst <b>nicht</b> gegen den Layout-String getestet („enthält 'masked'") —
/// das würde auch dann grün bleiben, wenn NLog den Renderer gar nicht kennt und
/// das Platzhalter-Konstrukt samt Nachrichtentext verschluckt. Stattdessen geht
/// ein echtes LogEventInfo durch das Layout, und geprüft wird, dass das
/// Geheimnis weg und der Rest lesbar ist.
/// </summary>
public class MaskingLayoutRendererTests
{
    public MaskingLayoutRendererTests()
    {
        // Ein typeof() lädt nur das Typ-Token und löst den ModuleInitializer
        // NICHT aus. Ohne diese Zeile sieht der Test dasselbe wie eine
        // Anwendung ganz ohne Registrierung.
        RuntimeHelpers.RunModuleConstructor(typeof(MaskingLayoutRenderer).Module.ModuleHandle);
    }

    private static string Render(string message)
    {
        Layout layout = Layout.FromString("${masked:inner=${message}}");
        return layout.Render(LogEventInfo.Create(LogLevel.Info, "test", message));
    }

    [Fact]
    public void Der_Renderer_ist_registriert_und_laesst_normalen_Text_durch()
    {
        // Fehlt die Registrierung, kommt hier ein "}" oder eine leere
        // Zeichenkette heraus — nicht der Text.
        Render("Benutzer mueller geladen.").Should().Be("Benutzer mueller geladen.");
    }

    [Theory]
    [InlineData("password=Geheim123", "Geheim123")]
    [InlineData("Passwort: Geheim123", "Geheim123")]
    [InlineData("pwd=Geheim123", "Geheim123")]
    [InlineData("token=abcdef0123", "abcdef0123")]
    [InlineData("apikey=sk-live-xyz", "sk-live-xyz")]
    public void Geheimnisse_verschwinden_aus_der_Meldung(string message, string secret)
    {
        string rendered = Render(message);

        rendered.Should().NotContain(secret);
        rendered.Should().Contain("***");
    }

    [Fact]
    public void Der_Rest_der_Meldung_bleibt_lesbar()
    {
        // Eine gierige Maskierung bis zum Zeilenende macht das Log unbrauchbar.
        string rendered = Render("Verbindung zu dc01 mit password=Geheim123 fehlgeschlagen (Code 49)");

        rendered.Should().Contain("Verbindung zu dc01");
        rendered.Should().Contain("fehlgeschlagen (Code 49)");
        rendered.Should().NotContain("Geheim123");
    }

    [Fact]
    public void DPAPI_Blobs_werden_maskiert()
    {
        string rendered = Render("Gespeichert: ENC1:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA");

        rendered.Should().Contain("ENC1:***");
        rendered.Should().NotContain("AQAAANCMnd8");
    }

    [Fact]
    public void Woerter_die_nur_aehnlich_aussehen_bleiben_unangetastet()
    {
        Render("passwordPolicy wurde gelesen").Should().Contain("passwordPolicy");
    }

    [Fact]
    public void Describe_nennt_die_Laenge_statt_des_Werts()
    {
        MaskingLayoutRenderer.Describe(null).Should().Be("(leer)");
        MaskingLayoutRenderer.Describe("").Should().Be("(leer)");
        MaskingLayoutRenderer.Describe("abc").Should().Be("(gesetzt, 3 Zeichen)").And.NotContain("abc");
    }
}
