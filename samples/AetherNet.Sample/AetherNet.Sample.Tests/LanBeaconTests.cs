// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Services;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The one line of text the whole LAN leg rests on.
///
/// <para>
/// Two phones on the same network find each other by broadcasting a beacon and answering the ones
/// they recognise. If that beacon is composed or read even slightly wrong, nothing throws and nothing
/// logs an error — the radio comes up, announces perfectly, hears everything, and links to nobody,
/// forever. That failure is indistinguishable from an empty network, which is why it is worth more
/// tests than its size suggests.
/// </para>
///
/// <para>
/// It reads from a broadcast port, which any application on the network can write to, so the parser
/// is fed hostile input by default rather than by accident.
/// </para>
/// </summary>
public class LanBeaconTests
{
    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public void A_beacon_survives_the_round_trip()
    {
        var text = LanBeacon.Compose("KXJB7MN2P4QRSTVW", 47821);

        Assert.True(LanBeacon.TryParse(text, out var address, out var port));
        Assert.Equal("KXJB7MN2P4QRSTVW", address);
        Assert.Equal(47821, port);
    }

    [Fact]
    public void A_real_rotating_address_survives_the_round_trip()
    {
        // The addresses this actually carries are ERIDs, not hand-written strings. A base-32 alphabet
        // has no space and no separator in it, which is the property the format depends on.
        var key = EphemeralRoutingId.DeriveRoutingKey(RandomNumberGenerator.GetBytes(32));
        var address = EphemeralRoutingId.Derive(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Assert.True(LanBeacon.TryParse(LanBeacon.Compose(address, 1234), out var read, out var port));
        Assert.Equal(address, read);
        Assert.Equal(1234, port);
    }

    [Fact]
    public void A_real_beacon_fits_well_inside_the_ceiling()
    {
        var key = EphemeralRoutingId.DeriveRoutingKey(RandomNumberGenerator.GetBytes(32));
        var address = EphemeralRoutingId.Derive(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // 65535 is the widest a port ever prints. If a real beacon were ever near the ceiling, the
        // ceiling would be rejecting real traffic rather than garbage.
        var longest = Encoding.ASCII.GetByteCount(LanBeacon.Compose(address, 65535));
        Assert.True(longest < LanBeacon.MaxLength / 2,
            $"a real beacon is {longest} bytes against a ceiling of {LanBeacon.MaxLength}");
    }

    // ── Everything else on the port ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("AETHER-LAN/2 ADDRESS 1234")]      // a version this build does not speak
    [InlineData("AETHER-LAN/1")]                   // prefix alone
    [InlineData("AETHER-LAN/1 ")]                  // prefix and nothing after it
    [InlineData("AETHER-LAN/1 ADDRESS")]           // no port
    [InlineData("AETHER-LAN/1  1234")]             // no address
    [InlineData("AETHER-LAN/1 ADDRESS notaport")]
    [InlineData("AETHER-LAN/1 ADDRESS 1234 EXTRA")]// a shape this version does not define
    [InlineData(" AETHER-LAN/1 ADDRESS 1234")]     // leading space — not our prefix
    [InlineData("aether-lan/1 ADDRESS 1234")]      // the prefix is ordinal, not case-insensitive
    public void Anything_that_is_not_a_beacon_is_refused(string? text)
        => Assert.False(LanBeacon.TryParse(text, out _, out _));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("99999999999")]                    // wider than an int
    [InlineData("+80")]                            // a sign is not a port
    [InlineData(" 80")]                            // nor is padding
    [InlineData("0x50")]
    public void A_port_that_is_not_a_port_is_refused(string port)
        => Assert.False(LanBeacon.TryParse($"AETHER-LAN/1 ADDRESS {port}", out _, out _));

    [Fact]
    public void A_datagram_longer_than_a_beacon_is_refused_without_being_parsed()
    {
        // Somebody else's traffic, or somebody having a go. The ceiling is checked before anything
        // else so a megabyte of text is rejected on its length rather than searched for a prefix.
        var flood = "AETHER-LAN/1 " + new string('A', LanBeacon.MaxLength * 4) + " 1234";
        Assert.False(LanBeacon.TryParse(flood, out _, out _));
    }

    [Theory]
    [InlineData("AETHER-LAN/1 ADDRESS 1234\n")]
    [InlineData("AETHER-LAN/1 ADDRESS 1234\r\n")]
    [InlineData("AETHER-LAN/1 ADDRESS 1234\0\0\0")]
    [InlineData("AETHER-LAN/1 ADDRESS 1234 ")]
    public void A_beacon_padded_the_way_real_senders_pad_still_reads(string text)
    {
        // A datagram read into a fixed buffer keeps whatever was after it, and other stacks terminate
        // with a newline out of habit. Refusing those would be refusing correct beacons.
        Assert.True(LanBeacon.TryParse(text, out var address, out var port));
        Assert.Equal("ADDRESS", address);
        Assert.Equal(1234, port);
    }

    // ── Composing badly is caught at the source ──────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_beacon_cannot_be_composed_without_an_address(string? address)
        => Assert.Throws<ArgumentException>(() => LanBeacon.Compose(address!, 1234));

    [Fact]
    public void An_address_with_a_space_in_it_is_refused_rather_than_silently_truncated()
    {
        // The format splits on a space. An address containing one would compose into something that
        // parses back as a DIFFERENT, shorter address — a beacon nobody can ever recognise, and no
        // error anywhere to say why.
        Assert.Throws<ArgumentException>(() => LanBeacon.Compose("ADDRESS WITH SPACE", 1234));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void A_beacon_cannot_be_composed_with_a_port_that_is_not_one(int port)
        => Assert.Throws<ArgumentOutOfRangeException>(() => LanBeacon.Compose("ADDRESS", port));

    // ── Who dials ────────────────────────────────────────────────────────────

    [Fact]
    public void Exactly_one_of_two_phones_dials()
    {
        Assert.True(LanBeacon.ShouldDial("B", "A"));
        Assert.False(LanBeacon.ShouldDial("A", "B"));
    }

    [Fact]
    public void A_phone_never_dials_itself()
    {
        // Its own broadcast comes straight back to it, twice every two seconds. Equal addresses are
        // not two phones.
        Assert.False(LanBeacon.ShouldDial("SAME", "SAME"));
    }

    [Theory]
    [InlineData(null, "A")]
    [InlineData("A", null)]
    [InlineData("", "A")]
    [InlineData("A", "")]
    public void Nothing_dials_on_a_missing_address(string? mine, string? theirs)
        => Assert.False(LanBeacon.ShouldDial(mine, theirs));

    [Fact]
    public void Two_real_phones_never_both_dial_and_never_both_wait()
    {
        // The rule has to hold for the addresses it will actually see, not just for "A" and "B". Both
        // dialling leaves a pair holding two links where one was wanted; neither dialling leaves them
        // beaconing at each other forever.
        for (var i = 0; i < 500; i++)
        {
            var a = EphemeralRoutingId.Derive(
                EphemeralRoutingId.DeriveRoutingKey(RandomNumberGenerator.GetBytes(32)), 1_700_000_000 + i);
            var b = EphemeralRoutingId.Derive(
                EphemeralRoutingId.DeriveRoutingKey(RandomNumberGenerator.GetBytes(32)), 1_700_000_000 + i);

            Assert.NotEqual(a, b);
            Assert.True(LanBeacon.ShouldDial(a, b) ^ LanBeacon.ShouldDial(b, a),
                $"both or neither would dial for {a} and {b}");
        }
    }

    [Fact]
    public void The_dial_rule_does_not_depend_on_the_machine_it_runs_on()
    {
        // CompareOrdinal, never Compare. A culture-aware comparison can order the same two strings
        // differently on two phones, and then both dial or neither does — a bug that would appear
        // only on somebody else's handset, in somebody else's country.
        Assert.True(LanBeacon.ShouldDial("a", "B"));   // ordinal: lowercase sorts after uppercase
        Assert.False(LanBeacon.ShouldDial("B", "a"));
    }
}
