// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using System.Net;
using System.Text;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Giving the app to a phone that has nothing, without anybody reading an address.
///
/// <para>
/// Every earlier attempt ended with a stranger looking at a raw IP under a browser's "not secure"
/// warning and deciding, correctly, to back away. Nothing that goes on a web page buys that trust
/// back, because the browser is simultaneously telling them not to trust us.
/// </para>
///
/// <para>
/// So the tap hands over a network with a person's name on it, their own phone notices there is no
/// internet behind it, and their own operating system raises the sign-in sheet everybody has seen in
/// a hotel. Three pieces have to be exactly right for that: the credentials in a format Android
/// already reads, a name that survives a Wi-Fi picker, and a DNS answer that makes the connectivity
/// probe land on us.
/// </para>
/// </summary>
public class GuestHandoverTests
{
    // ── The credentials the tap carries ──────────────────────────────────────

    [Fact]
    public void Credentials_survive_the_round_trip()
    {
        var made = WifiHandover.Credentials("DIRECT-Aether Thabo", "K9MP2QRS7TVW3XYZ");
        var read = WifiHandover.Read(made);

        Assert.NotNull(read);
        Assert.Equal("DIRECT-Aether Thabo", read.Value.Ssid);
        Assert.Equal("K9MP2QRS7TVW3XYZ", read.Value.Passphrase);
    }

    [Fact]
    public void The_record_says_it_is_wifi_credentials()
    {
        // Android reads this by MIME type. Anything else and the tap is a tag nobody acts on.
        var message = WifiHandover.Message("DIRECT-Aether", "K9MP2QRS7TVW3XYZ");
        Assert.Contains(WifiHandover.MimeType, Encoding.ASCII.GetString(message), StringComparison.Ordinal);

        // Media-type record: message begin, message end, short record, TNF 0x02.
        Assert.Equal(0x80, message[0] & 0x80);
        Assert.Equal(0x40, message[0] & 0x40);
        Assert.Equal(0x10, message[0] & 0x10);
        Assert.Equal(0x02, message[0] & 0x07);
    }

    [Fact]
    public void The_fields_are_big_endian_the_way_the_spec_says()
    {
        // The opposite byte order to everything else this app writes. Swapped here, the record is one
        // a phone silently ignores — no error, no dispatch, nothing to debug from.
        var made = WifiHandover.Credentials("X", "12345678");

        Assert.Equal(0x10, made[0]);      // Credential attribute, 0x100E
        Assert.Equal(0x0E, made[1]);
        Assert.Equal(made.Length - 4, (made[2] << 8) | made[3]);
    }

    [Fact]
    public void The_real_mac_address_is_never_broadcast()
    {
        // A MAC on a tap is a stable identifier for this handset handed to anyone who reads it —
        // exactly what the rotating wire address exists to prevent.
        var made = WifiHandover.Credentials("DIRECT-Aether", "K9MP2QRS7TVW3XYZ");
        Assert.Contains(new byte[6], SplitSixes(made));
    }

    private static IEnumerable<byte[]> SplitSixes(byte[] data)
    {
        for (var i = 0; i + 6 <= data.Length; i++) yield return data[i..(i + 6)];
    }

    [Theory]
    [InlineData("", "12345678")]                                     // no name
    [InlineData("net", "short")]                                     // under WPA2's floor
    [InlineData("net", "")]
    public void Credentials_that_no_phone_could_use_are_refused(string ssid, string pass)
        => Assert.Throws<ArgumentException>(() => WifiHandover.Credentials(ssid, pass));

    [Fact]
    public void A_name_or_key_beyond_what_wifi_allows_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            WifiHandover.Credentials(new string('x', WifiHandover.LongestSsid + 1), "12345678"));
        Assert.Throws<ArgumentException>(() =>
            WifiHandover.Credentials("net", new string('x', WifiHandover.LongestPassphrase + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x10, 0x0E, 0xFF, 0xFF, 0x00 })]        // a length running off the end
    public void Reading_something_that_is_not_credentials_gives_nothing(byte[]? data)
        => Assert.Null(WifiHandover.Read(data));

    [Fact]
    public void Reading_nonsense_never_throws()
    {
        var random = new Random(20260825);
        for (var i = 0; i < 5000; i++)
        {
            var junk = new byte[random.Next(0, 96)];
            random.NextBytes(junk);
            WifiHandover.Read(junk);
        }
    }

    // ── The name a person actually reads ─────────────────────────────────────

    [Fact]
    public void The_network_is_called_after_the_person_offering()
    {
        var net = GuestGroup.For("KXJB7-MN2P4");

        Assert.StartsWith(GuestGroup.RequiredPrefix, net.NetworkName, StringComparison.Ordinal);
        Assert.Contains("Aether", net.NetworkName, StringComparison.Ordinal);
        Assert.Contains("KXJB7-MN2P4", net.NetworkName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_fits_what_android_allows()
    {
        // Thirty-two characters including a prefix we cannot opt out of. A name over the line is not
        // truncated by Android — setNetworkName rejects it and no group forms at all.
        foreach (var who in new[] { "KXJB7-MN2P4", new string('W', 80), "", null })
        {
            var net = GuestGroup.For(who);
            Assert.True(net.NetworkName.Length <= GuestGroup.LongestName,
                $"the name {net.NetworkName} is {net.NetworkName.Length} characters");
            Assert.StartsWith(GuestGroup.RequiredPrefix, net.NetworkName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_name_with_nothing_usable_in_it_still_reads_as_Aether()
    {
        // Somebody whose tag has not come up yet, or a name that is all emoji. A box in a Wi-Fi
        // picker is not a person, so anything that would render as one is dropped.
        foreach (var unusable in new[] { null, "", "   ", "😀😀😀", "!!!" })
            Assert.Equal(GuestGroup.Anonymous, GuestGroup.Label(unusable));
    }

    [Fact]
    public void Every_handover_gets_its_own_key()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++) Assert.True(seen.Add(GuestGroup.Passphrase()));
    }

    [Fact]
    public void The_key_is_one_wpa2_will_accept()
    {
        var pass = GuestGroup.Passphrase();
        Assert.InRange(pass.Length, WifiHandover.ShortestPassphrase, WifiHandover.LongestPassphrase);

        // And the credentials it goes into must actually build, which is the only test that matters.
        Assert.NotNull(WifiHandover.Read(WifiHandover.Credentials(GuestGroup.For("X").NetworkName, pass)));
    }

    [Fact]
    public void A_whole_handover_fits_on_a_tap()
    {
        var net = GuestGroup.For("KXJB7-MN2P4");
        var message = WifiHandover.Message(net.NetworkName, net.Passphrase);

        // One short NDEF record carries at most 255 bytes of payload. Over that and the tap silently
        // carries nothing.
        Assert.True(message.Length < 255, $"a handover is {message.Length} bytes");
    }

    // ── Making their own phone ask the question ──────────────────────────────

    [Fact]
    public void Every_name_a_guest_looks_up_resolves_to_us()
    {
        // That is what a captive portal is. The network exists for two minutes and for one purpose;
        // the alternative is their connectivity probe reaching the real internet and their phone
        // concluding everything is fine.
        var reply = CaptivePortal.Answer(Query("connectivitycheck.gstatic.com"), IPAddress.Parse("192.168.49.1"));

        Assert.NotNull(reply);
        Assert.Equal(0x8180, (reply[2] << 8) | reply[3]);          // a response, no error
        Assert.Equal(1, (reply[6] << 8) | reply[7]);               // exactly one answer
        Assert.Equal(new byte[] { 192, 168, 49, 1 }, reply[^4..]); // pointing at us
    }

    [Fact]
    public void The_answer_keeps_the_question_the_phone_asked()
    {
        // A reply whose id or question does not match is one the phone throws away, and it looks
        // exactly like a network that never answered.
        var query = Query("example.com");
        var reply = CaptivePortal.Answer(query, IPAddress.Parse("10.0.0.1"))!;

        Assert.Equal(query[0], reply[0]);                          // the transaction id
        Assert.Equal(query[1], reply[1]);

        // And the question itself, copied through untouched. The header in between is deliberately
        // rewritten — flags to "a response", answer count to one — so only these two parts match.
        Assert.Equal(query[12..], reply[12..query.Length]);
    }

    [Fact]
    public void A_response_is_never_answered_again()
    {
        // Answering a response is how two resolvers talk each other into a loop.
        var response = Query("example.com");
        response[2] = 0x81;
        Assert.Null(CaptivePortal.Answer(response, IPAddress.Parse("10.0.0.1")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Something_that_is_not_a_question_is_not_answered(byte[]? query)
        => Assert.Null(CaptivePortal.Answer(query, IPAddress.Parse("10.0.0.1")));

    [Fact]
    public void Answering_nonsense_never_throws()
    {
        var random = new Random(20260825);
        var us = IPAddress.Parse("192.168.49.1");

        for (var i = 0; i < 5000; i++)
        {
            var junk = new byte[random.Next(0, 96)];
            random.NextBytes(junk);
            CaptivePortal.Answer(junk, us);
        }
    }

    [Theory]
    [InlineData("/generate_204")]
    [InlineData("/gen_204")]
    [InlineData("/connecttest.txt")]
    [InlineData("/ncsi.txt")]
    [InlineData("/hotspot-detect.html")]
    public void A_connectivity_probe_is_recognised(string path)
        => Assert.True(CaptivePortal.IsProbe(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/tmb/00112233445566778899aabbccddeeff")]
    [InlineData("/tmb/00112233445566778899aabbccddeeff/aether.apk")]
    public void The_card_and_the_package_are_not_probes(string? path)
        => Assert.False(CaptivePortal.IsProbe(path));

    [Fact]
    public void The_probe_is_sent_onward_rather_than_answered()
    {
        // A redirect, not the page. Android takes the Location and opens it in its own portal window,
        // and that window — system-drawn, titled with the network's name — is the entire point.
        // Answering with the page gets it rendered inside the probe, where nobody sees it.
        var sent = CaptivePortal.RedirectTo("http://192.168.49.1:8080/tmb/abc");

        Assert.StartsWith("HTTP/1.1 302", sent, StringComparison.Ordinal);
        Assert.Contains("Location: http://192.168.49.1:8080/tmb/abc", sent, StringComparison.Ordinal);
        Assert.Contains("no-store", sent, StringComparison.Ordinal);
    }

    /// <summary>One standard A query for a name.</summary>
    private static byte[] Query(string name)
    {
        var q = new List<byte> { 0xAB, 0xCD, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        foreach (var label in name.Split('.'))
        {
            q.Add((byte)label.Length);
            q.AddRange(Encoding.ASCII.GetBytes(label));
        }

        q.Add(0x00);
        q.AddRange([0x00, 0x01, 0x00, 0x01]);   // A, IN
        return q.ToArray();
    }
}
