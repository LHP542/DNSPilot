using System.Runtime.Versioning;
using FluentAssertions;
using DnsPilot.Services;

namespace DnsPilot.Tests;

/// <summary>
/// DPAPI-Schutz des optionalen Admin-Kennworts. Die Tests laufen nur unter
/// Windows — DnsPilot ist ohnehin Windows-only, aber der Testlauf soll auf einem
/// anderen System nicht mit einer Ausnahme abbrechen.
/// </summary>
[SupportedOSPlatform("windows")]
public class SecretProtectionTests
{
    [Fact]
    public void Protect_und_Unprotect_sind_ein_Rundlauf()
    {
        WindowsOnly.Require(OperatingSystem.IsWindows());

        string? cipher = SecretProtection.Protect("Geheim!123äöü");

        cipher.Should().StartWith(SecretProtection.Prefix);
        SecretProtection.Unprotect(cipher).Should().Be("Geheim!123äöü");
    }

    [Fact]
    public void Protect_speichert_das_Passwort_nicht_im_Klartext()
    {
        WindowsOnly.Require(OperatingSystem.IsWindows());

        SecretProtection.Protect("HalloWelt")!.Should().NotContain("HalloWelt");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_laesst_Leereingaben_null(string? value) =>
        SecretProtection.Protect(value).Should().BeNull();

    [Fact]
    public void Unprotect_ignoriert_einen_Wert_ohne_Praefix()
    {
        // Wer das Passwort von Hand in die JSON schreibt, soll keinen
        // Klartext-Bind auslösen.
        SecretProtection.Unprotect("Klartext").Should().BeNull();
    }

    [Fact]
    public void Unprotect_verkraftet_kaputte_Base64_Daten()
    {
        WindowsOnly.Require(OperatingSystem.IsWindows());

        SecretProtection.Unprotect(SecretProtection.Prefix + "das-ist-kein-base64!!").Should().BeNull();
    }

    [Fact]
    public void Unprotect_verkraftet_gueltiges_Base64_das_kein_DPAPI_Blob_ist()
    {
        WindowsOnly.Require(OperatingSystem.IsWindows());

        string fake = SecretProtection.Prefix + Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]);

        SecretProtection.Unprotect(fake).Should().BeNull();
    }

    [Theory]
    [InlineData("ENC1:abc", true)]
    [InlineData("Klartext", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProtected_erkennt_das_Praefix(string? value, bool expected) =>
        SecretProtection.IsProtected(value).Should().Be(expected);
}

/// <summary>
/// Kleiner Ersatz für ein Skip-Attribut. Bewusst nicht „Skip" genannt — der
/// Name kollidiert mit xunit-eigenen Typen.
/// </summary>
internal static class WindowsOnly
{
    public static void Require(bool condition)
    {
        if (!condition) Assert.Skip("Dieser Test setzt Windows voraus.");
    }
}
