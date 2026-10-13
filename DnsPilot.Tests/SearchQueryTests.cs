using FluentAssertions;
using DnsPilot.Services;

namespace DnsPilot.Tests;

/// <summary>Deutung des Suchfelds: Adresse, Netz, Bereich, Platzhalter oder Freitext.</summary>
public class SearchQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Leere_Eingabe_zeigt_alles(string? input) =>
        SearchQuery.Parse(input).Kind.Should().Be(SearchKind.All);

    [Fact]
    public void Eine_Adresse_ist_ein_Bereich_aus_einer_Adresse()
    {
        var q = SearchQuery.Parse("172.16.1.11");

        q.Kind.Should().Be(SearchKind.Range);
        q.Range!.Value.Count.Should().Be(1);
        q.Range.Value.ToString().Should().Be("172.16.1.11");
    }

    [Fact]
    public void CIDR_wird_auf_die_Netzgrenze_gezogen()
    {
        var q = SearchQuery.Parse("172.16.1.77/24");

        q.Range!.Value.ToString().Should().Be("172.16.1.0/24");
        q.Range.Value.Count.Should().Be(256);
    }

    [Theory]
    [InlineData("172.16.1.10-50", "172.16.1.10", "172.16.1.50")]
    [InlineData("172.16.1.10 - 172.16.1.50", "172.16.1.10", "172.16.1.50")]
    [InlineData("10.0.0.250-10.0.1.5", "10.0.0.250", "10.0.1.5")]
    public void Bereiche_werden_erkannt(string input, string first, string last)
    {
        var range = SearchQuery.Parse(input).Range!.Value;

        Ipv4.ToText(range.First).Should().Be(first);
        Ipv4.ToText(range.Last).Should().Be(last);
    }

    [Theory]
    [InlineData("172.16.1.*", "172.16.1.0/24")]
    [InlineData("172.16.*", "172.16.0.0/16")]
    [InlineData("10.*", "10.0.0.0/8")]
    public void Platzhalter_werden_zu_Netzen(string input, string expected) =>
        SearchQuery.Parse(input).Range!.Value.ToString().Should().Be(expected);

    [Theory]
    [InlineData("drucker")]
    [InlineData("172.16.1")]          // angefangen — bleibt Freitext
    [InlineData("10")]                // IPAddress.TryParse nähme das als 0.0.0.10
    [InlineData("172.16.1.300")]
    [InlineData("172.16.1.50-10")]    // rückwärts
    [InlineData("172.16.1.0/4")]      // zu groß, eher Tippfehler
    [InlineData("pc-0412")]           // Bindestrich, aber keine Adresse
    public void Alles_andere_ist_Freitext(string input) =>
        SearchQuery.Parse(input).Kind.Should().Be(SearchKind.Text);
}

/// <summary>Zahlendarstellung von IPv4-Adressen und Bereiche.</summary>
public class IpRangeTests
{
    [Fact]
    public void Umrechnung_ist_ein_Rundlauf()
    {
        Ipv4.TryParseStrict("172.16.1.11", out uint value).Should().BeTrue();

        Ipv4.ToText(value).Should().Be("172.16.1.11");
        Ipv4.FromUInt32(value).ToString().Should().Be("172.16.1.11");
        Ipv4.TryToUInt32(Ipv4.FromUInt32(value), out uint back).Should().BeTrue();
        back.Should().Be(value);
    }

    [Fact]
    public void IPv6_ist_keine_IPv4_Adresse() =>
        Ipv4.TryToUInt32(System.Net.IPAddress.IPv6Loopback, out _).Should().BeFalse();

    [Fact]
    public void Nutzbare_Adressen_lassen_Netz_und_Broadcast_aus()
    {
        Ipv4.TryParseStrict("192.168.0.0", out uint net);
        var range = IpRange.FromCidr(net, 29);

        range.UsableAddresses().Select(Ipv4.ToText).Should()
            .Equal("192.168.0.1", "192.168.0.2", "192.168.0.3", "192.168.0.4", "192.168.0.5", "192.168.0.6");
    }

    [Fact]
    public void Ein_frei_gewaehlter_Bereich_ist_vollstaendig_nutzbar()
    {
        Ipv4.TryParseStrict("10.0.0.0", out uint first);
        new IpRange(first, first + 2).UsableAddresses().Should().HaveCount(3);
    }

    [Fact]
    public void Slash32_ist_eine_einzelne_Adresse()
    {
        Ipv4.TryParseStrict("10.1.2.3", out uint ip);
        var range = IpRange.FromCidr(ip, 32);

        range.Count.Should().Be(1);
        range.UsableAddresses().Should().Equal(ip);
    }
}

/// <summary>Rechnen mit in-addr.arpa.</summary>
public class ReverseZoneTests
{
    [Theory]
    [InlineData("1.16.172.in-addr.arpa", "172.16.1.0/24")]
    [InlineData("16.172.in-addr.arpa", "172.16.0.0/16")]
    [InlineData("10.in-addr.arpa", "10.0.0.0/8")]
    [InlineData("1.16.172.IN-ADDR.ARPA.", "172.16.1.0/24")]
    public void Reverse_Zonen_werden_zu_Netzen(string zone, string expected)
    {
        ReverseZone.TryGetNetwork(zone, out var network).Should().BeTrue();
        network.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("lhp.intern")]
    [InlineData("0-25.1.16.172.in-addr.arpa")]   // RFC 2317 — bewusst nicht unterstützt
    [InlineData("in-addr.arpa")]
    [InlineData("01.16.172.in-addr.arpa")]
    [InlineData("1.0.0.0.ip6.arpa")]
    public void Anderes_ist_kein_IPv4_Netz(string zone) =>
        ReverseZone.TryGetNetwork(zone, out _).Should().BeFalse();

    [Fact]
    public void PTR_Name_und_Adresse_sind_ein_Rundlauf()
    {
        Ipv4.TryParseStrict("172.16.1.11", out uint ip);
        string owner = ReverseZone.PtrOwnerName(ip);

        owner.Should().Be("11.1.16.172.in-addr.arpa");
        ReverseZone.TryParsePtrOwner(owner, out var back).Should().BeTrue();
        back.ToString().Should().Be("172.16.1.11");
    }

    [Fact]
    public void Ein_unvollstaendiger_PTR_Name_ist_keine_Adresse() =>
        ReverseZone.TryParsePtrOwner("1.16.172.in-addr.arpa", out _).Should().BeFalse();
}
