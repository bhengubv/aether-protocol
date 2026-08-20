// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The codec has to fit the radio carrying the call, not a constant.
///
/// <para>
/// A fixed 24 kbps is fine right up until the link changes underneath it — which is exactly what
/// automatic radio handover does on purpose. Measured on real phones: Wi-Fi Direct carries a call
/// comfortably, BLE as currently used manages about 11 kbps in one direction. Asking the second for
/// the first's bitrate does not merely sound bad, it starves the return direction — one side sent
/// happily at fifty frames a second while the other could not get a single write in.
/// </para>
/// </summary>
public class CodecFitsTheRadioTests
{
    // ── a link that will not say ───────────────────────────────────────────

    /// <summary>
    /// An unknown link gets the full default rather than the floor. A radio that cannot carry it will
    /// drop frames, which recovers; a call needlessly encoded at 8 kbps sounds bad for its whole
    /// length and never recovers.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_unknown_link_gets_the_full_rate(long linkBps)
        => Assert.Equal(OpusVoiceCodec.DefaultBitrateBps, OpusVoiceCodec.BitrateFor(linkBps));

    // ── a link with room ───────────────────────────────────────────────────

    /// <summary>Wi-Fi Direct has room to spare, so nothing is given up.</summary>
    [Theory]
    [InlineData(250_000_000)]   // Wi-Fi Direct, as declared
    [InlineData(1_000_000)]
    [InlineData(72_000)]        // exactly three times the default
    public void A_wide_link_gets_the_full_rate(long linkBps)
        => Assert.Equal(OpusVoiceCodec.DefaultBitrateBps, OpusVoiceCodec.BitrateFor(linkBps));

    // ── a link without ─────────────────────────────────────────────────────

    /// <summary>A narrow link gets a third of itself — never all of it.</summary>
    [Theory]
    [InlineData(45_000, 15_000)]
    [InlineData(36_000, 12_000)]
    [InlineData(30_000, 10_000)]
    public void A_narrow_link_gets_a_third_of_itself(long linkBps, int expected)
        => Assert.Equal(expected, OpusVoiceCodec.BitrateFor(linkBps));

    /// <summary>
    /// BLE as measured on these handsets — about 11 kbps. A call over it is encoded at the floor
    /// rather than at four times what the link can carry.
    /// </summary>
    [Fact]
    public void The_measured_ble_link_gets_the_floor()
    {
        var chosen = OpusVoiceCodec.BitrateFor(11_000);

        Assert.Equal(OpusVoiceCodec.MinBitrateBps, chosen);
        Assert.True(chosen < OpusVoiceCodec.DefaultBitrateBps,
            "a link measured at 11 kbps must not be asked for 24");
    }

    /// <summary>Never below the floor, however hopeless the link. Below this, use a voice note.</summary>
    [Theory]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(100)]
    public void Nothing_goes_below_the_floor(long linkBps)
        => Assert.Equal(OpusVoiceCodec.MinBitrateBps, OpusVoiceCodec.BitrateFor(linkBps));

    // ── the shape of it ────────────────────────────────────────────────────

    /// <summary>Always within Opus's usable range, whatever the radio claims.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(11_000)]
    [InlineData(48_000)]
    [InlineData(250_000_000)]
    [InlineData(long.MaxValue)]
    public void The_answer_is_always_usable(long linkBps)
    {
        var chosen = OpusVoiceCodec.BitrateFor(linkBps);

        Assert.InRange(chosen, OpusVoiceCodec.MinBitrateBps, OpusVoiceCodec.DefaultBitrateBps);
    }

    /// <summary>A wider link never gets a lower bitrate than a narrower one.</summary>
    [Fact]
    public void A_wider_link_never_does_worse()
    {
        long[] links = [6_000, 11_000, 24_000, 36_000, 60_000, 200_000, 250_000_000];

        var chosen = links.Select(OpusVoiceCodec.BitrateFor).ToArray();

        Assert.Equal(chosen.OrderBy(x => x), chosen);
    }

    // ── the floor: which links can carry a call at all ─────────────────────

    /// <summary>
    /// The floor is not a separate opinion — it falls out of the other two constants. A call may have
    /// a third of the link, and the lowest rate worth encoding is the floor, so the narrowest usable
    /// link is three times that. Pinned so the three can never drift apart silently.
    /// </summary>
    [Fact]
    public void The_narrowest_usable_link_is_three_times_the_floor()
        => Assert.Equal(OpusVoiceCodec.MinBitrateBps * 3, OpusVoiceCodec.MinLinkBpsForCall);

    /// <summary>Wi-Fi Direct clears it by orders of magnitude — that is the measured result.</summary>
    [Theory]
    [InlineData(250_000_000)]   // Wi-Fi Direct, as declared
    [InlineData(100_000)]
    [InlineData(24_000)]        // exactly the floor
    public void A_link_at_or_above_the_floor_can_carry_a_call(long linkBps)
        => Assert.True(OpusVoiceCodec.CanCarryCall(linkBps));

    /// <summary>
    /// BLE cannot, at the figure measured on these handsets. This is the whole finding: 11 kbps is
    /// not a degraded call, it is no call, and the stack has to be able to say which.
    /// </summary>
    [Theory]
    [InlineData(11_000)]        // BLE, measured 2026-08-20
    [InlineData(23_999)]        // one short of the floor
    [InlineData(5_000)]
    [InlineData(1)]
    public void A_link_below_the_floor_cannot(long linkBps)
        => Assert.False(OpusVoiceCodec.CanCarryCall(linkBps));

    /// <summary>
    /// A radio that will not say gets the benefit of the doubt, the same way the bitrate does.
    /// Refusing a call on no evidence is worse than attempting one that then struggles honestly.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_link_that_will_not_say_is_given_the_benefit_of_the_doubt(long linkBps)
        => Assert.True(OpusVoiceCodec.CanCarryCall(linkBps));

    /// <summary>
    /// Below the floor the bitrate is clamped UP — the encoder asks for more than the link can move.
    /// That is precisely why the floor has to exist as its own check: the bitrate alone looks
    /// perfectly reasonable while describing a call that cannot happen.
    /// </summary>
    [Fact]
    public void Below_the_floor_the_bitrate_asks_for_more_than_the_link_has()
    {
        const long ble = 11_000;

        Assert.False(OpusVoiceCodec.CanCarryCall(ble));
        Assert.True(OpusVoiceCodec.BitrateFor(ble) > ble / 3);
        Assert.Equal(OpusVoiceCodec.MinBitrateBps, OpusVoiceCodec.BitrateFor(ble));
    }

    /// <summary>And a codec really can be built at whatever it picks — the range is not theoretical.</summary>
    [Theory]
    [InlineData(11_000)]
    [InlineData(45_000)]
    [InlineData(250_000_000)]
    public void The_codec_builds_at_the_chosen_rate(long linkBps)
    {
        using var codec = new OpusVoiceCodec(bitrateBps: OpusVoiceCodec.BitrateFor(linkBps));

        var frame = new short[codec.FrameSamples];
        var encoded = codec.Encode(frame);

        Assert.NotEmpty(encoded);
    }
}
