// SPDX-License-Identifier: MIT

using AetherNetNodeService.Host;
using System.Net;
using System.Text;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

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
