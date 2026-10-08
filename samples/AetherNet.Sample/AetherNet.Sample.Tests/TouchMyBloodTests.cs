// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Touch My Blood — handing the app to a phone that has nothing on it.
///
/// <para>
/// Every piece of this is fed by somebody who does not have our software. The tap is read by a stock
/// handset's NFC stack, the address is fetched by a stock browser, and the server answering is open
/// on a network anybody can be sitting on. So none of it can assume a well-behaved caller, and all of
/// it has to be exactly right the first time — the moment it runs is a person standing in front of a
/// friend, and there is no second attempt that does not feel like a broken promise.
/// </para>
/// </summary>
public class TouchMyBloodTests
{
    // ── The bytes that cross on the tap ──────────────────────────────────────

    [Fact]
    public void A_uri_record_survives_the_round_trip()
    {
        const string url = "http://192.168.0.115:41234/tmb/00112233445566778899aabbccddeeff/aether.apk";
        Assert.Equal(url, Ndef.ReadUri(Ndef.Uri(url)));
    }

    [Fact]
    public void The_common_prefix_is_abbreviated_to_one_byte()
    {
        var message = Ndef.Uri("http://example.com");

        // Header, type length, payload length, 'U', then the abbreviation code.
        Assert.Equal(0x55, message[3]);
        Assert.Equal(0x03, message[4]);                       // 0x03 == "http://"
        Assert.DoesNotContain("http", Encoding.ASCII.GetString(message), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://www.example.com", (byte)0x02)]
    [InlineData("http://www.example.com", (byte)0x01)]
    [InlineData("https://example.com", (byte)0x04)]
    [InlineData("http://example.com", (byte)0x03)]
    public void The_longest_prefix_wins(string url, byte expected)
    {
        // "https://www.x" encoded as "https://" plus a literal "www.x" still parses and still points
        // at the right place — but only the longest match round-trips byte for byte, and a tag that
        // does not round-trip is one nobody can check without two phones.
        Assert.Equal(expected, Ndef.Uri(url)[4]);
        Assert.Equal(url, Ndef.ReadUri(Ndef.Uri(url)));
    }

    [Fact]
    public void A_uri_with_no_known_prefix_still_travels()
    {
        const string odd = "aether://KXJB7-MN2P4";
        Assert.Equal(0x00, Ndef.Uri(odd)[4]);
        Assert.Equal(odd, Ndef.ReadUri(Ndef.Uri(odd)));
    }

    [Fact]
    public void A_tap_carries_who_you_are_and_nothing_else()
    {
        // Android dispatches on the FIRST record and never looks past it. This message used to lead
        // with a web address, so every tap went to a browser and the intent filter claiming our own
        // record could never fire — a claim wired to nothing.
        var message = Ndef.Tag("KXJB7-MN2P4");

        Assert.Equal("KXJB7-MN2P4", Ndef.ReadTag(message));
        Assert.Null(Ndef.ReadUri(message));
        Assert.Contains(Ndef.TagRecordType, Encoding.ASCII.GetString(message), StringComparison.Ordinal);
    }

    [Fact]
    public void The_identity_is_the_first_and_only_record()
    {
        var message = Ndef.Tag("KXJB7-MN2P4");

        // Message begin AND message end both on record one: there is no second record for a browser
        // to be dispatched on.
        Assert.Equal(0x80, message[0] & 0x80);
        Assert.Equal(0x40, message[0] & 0x40);
        Assert.Equal(0x04, message[0] & 0x07);       // an external type — ours, not a well-known one

        Assert.Equal(3 + Ndef.TagRecordType.Length + "KXJB7-MN2P4".Length, message.Length);
    }

    [Fact]
    public void A_tap_that_says_nothing_is_refused()
    {
        foreach (var nothing in new[] { null, "", "   " })
            Assert.Throws<ArgumentException>(() => Ndef.Tag(nothing!));
    }

    [Fact]
    public void Reading_a_tap_that_is_not_ours_gives_nothing()
    {
        // Somebody else's tag, a poster, a bus pass. All of it comes through the same reader.
        Assert.Null(Ndef.ReadTag(Ndef.Uri("http://example.com")));
        Assert.Null(Ndef.ReadTag(null));
        Assert.Null(Ndef.ReadTag([]));
        Assert.Null(Ndef.ReadTag([0xD1, 0x01]));
    }

    [Fact]
    public void Reading_a_tap_never_throws_on_nonsense()
    {
        var random = new Random(20260824);
        for (var i = 0; i < 5000; i++)
        {
            var junk = new byte[random.Next(0, 64)];
            random.NextBytes(junk);
            Ndef.ReadTag(junk);      // a tag, or null. Never an exception.
        }
    }
}
