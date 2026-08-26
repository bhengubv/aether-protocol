// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Choosing the group's channel so the person joining keeps their internet.
///
/// <para>
/// Measured on a P30 whose own Wi-Fi was on 5500 MHz — a radar channel a group owner is barred from.
/// We asked for the 5 GHz band, the radio refused, and the retry dropped every preference. The group
/// went up on 2.4 GHz against a 5 GHz house network, and the phone joining had to leave its Wi-Fi to
/// follow. Its internet went, which is the exact moment somebody being helped decides they have been
/// broken instead.
/// </para>
///
/// <para>
/// The fault was the shape of the retry — one preference, then none. These test the ladder that
/// replaced it.
/// </para>
/// </summary>
public class GroupChannelTests
{
    /// <summary>
    /// <b>The measured case.</b> A radar channel is never asked for.
    /// </summary>
    [Fact]
    public void A_radar_channel_is_never_the_first_rung()
    {
        var ladder = GroupChannel.Ladder(5500);

        Assert.DoesNotContain(5500, ladder);
        Assert.False(GroupChannel.Allowed(5500));
        Assert.True(GroupChannel.IsRadar(5500));
    }

    /// <summary>And the first thing tried instead stays in the same band.</summary>
    [Fact]
    public void From_a_radar_channel_the_first_try_stays_in_the_same_band()
    {
        Assert.True(GroupChannel.Ladder(5500)[0] > GroupChannel.LowBandTo,
            "leaving 5GHz is what costs the other phone its network");
    }

    /// <summary>A legal channel is asked for exactly, so nothing has to move.</summary>
    [Theory]
    [InlineData(2412)]
    [InlineData(2437)]
    [InlineData(5180)]
    [InlineData(5745)]
    [InlineData(5825)]
    public void A_legal_channel_is_asked_for_exactly(int station)
    {
        Assert.Equal(station, GroupChannel.Ladder(station)[0]);
    }

    /// <summary>The radar range is where the specification says it is.</summary>
    [Theory]
    [InlineData(5260, true)]
    [InlineData(5500, true)]
    [InlineData(5720, true)]
    [InlineData(5240, false)]
    [InlineData(5745, false)]
    [InlineData(2437, false)]
    public void The_radar_range_is_where_it_should_be(int mhz, bool radar)
    {
        Assert.Equal(radar, GroupChannel.IsRadar(mhz));
        Assert.Equal(!radar, GroupChannel.Allowed(mhz));
    }

    /// <summary>
    /// The ladder is ordered by what it costs the person joining.
    /// </summary>
    /// <remarks>
    /// Same channel costs nothing, same band costs a channel change, the other band costs them the
    /// network. Getting this order wrong is the bug, restated.
    /// </remarks>
    [Theory]
    [InlineData(5180)]
    [InlineData(5500)]
    [InlineData(2437)]
    public void The_ladder_is_ordered_by_what_it_costs_the_other_phone(int station)
    {
        var ladder = GroupChannel.Ladder(station);
        var sameBand = station > GroupChannel.LowBandTo;

        // Nothing moves, when that is available at all.
        if (GroupChannel.Allowed(station)) Assert.Equal(station, ladder[0]);

        // Every rung that keeps their band comes before every rung that costs it. Pinning indices
        // would only be testing how many channels we happen to try, which is not the property.
        var lastSameBand = -1;
        var firstOtherBand = int.MaxValue;

        for (var i = 0; i < ladder.Length; i++)
        {
            if (ladder[i] == GroupChannel.Anything) continue;
            if ((ladder[i] > GroupChannel.LowBandTo) == sameBand) lastSameBand = i;
            else firstOtherBand = Math.Min(firstOtherBand, i);
        }

        Assert.True(lastSameBand < firstOtherBand,
            "a rung that costs them their network must never come before one that does not");
    }

    /// <summary>
    /// A refusal on one 5 GHz channel does not end the band.
    /// </summary>
    /// <remarks>
    /// The measured mistake: 5745 was refused and I read it as "this phone cannot host on 5 GHz".
    /// Which channels a group owner may occupy varies by chipset, and channel 36 is the one most
    /// commonly granted — in some documented cases the only one.
    /// </remarks>
    [Fact]
    public void One_refusal_is_not_the_whole_band()
    {
        var ladder = GroupChannel.Ladder(5500);
        var fiveGhz = ladder.Where(c => c > GroupChannel.LowBandTo).ToArray();

        Assert.True(fiveGhz.Length > 1, "one 5GHz channel is not an attempt at the band");
        Assert.Equal(5180, fiveGhz[0]);
    }

    /// <summary>
    /// <b>It never offers a rung in the other band.</b>
    /// </summary>
    /// <remarks>
    /// A phone has one radio. Hosting in the band the other phone is not in costs them their network,
    /// and that is not worth having at any price — measured on a Redmi, internet gone on every single
    /// handover. There is also no "whatever the radio gives" rung, because what it gives is 2.4GHz.
    /// If nothing in-band can be had, hosting fails and says so.
    /// </remarks>
    [Theory]
    [InlineData(5500)]
    [InlineData(5180)]
    [InlineData(5745)]
    public void The_band_that_costs_them_their_wifi_is_tried_last(int station)
    {
        var ladder = GroupChannel.Ladder(station);

        Assert.True(ladder[^1] < GroupChannel.LowBandTo,
            "the rung that costs them their network belongs at the very end");
        Assert.True(ladder.Length > 1, "and never as the only option");
    }

    /// <summary>A 2.4 GHz phone is served from its own band first.</summary>
    [Fact]
    public void A_two_point_four_phone_is_served_from_its_own_band_first()
    {
        Assert.True(GroupChannel.Ladder(2412)[0] < GroupChannel.LowBandTo);
    }

    /// <summary>
    /// With no Wi-Fi of our own, it still refuses to sit on 2.4 GHz.
    /// </summary>
    /// <remarks>
    /// Not knowing our own channel is not a reason to take the one that breaks theirs. Their phone is
    /// far more likely to be on 5 GHz than to be on nothing.
    /// </remarks>
    [Fact]
    public void With_no_wifi_it_tries_five_first_and_keeps_two_point_four_in_reserve()
    {
        var ladder = GroupChannel.Ladder(0);

        Assert.True(ladder[0] > GroupChannel.LowBandTo, "their phone is likelier to be on 5GHz");
        Assert.True(ladder[^1] < GroupChannel.LowBandTo, "but something must still work");
    }

    /// <summary>No rung is ever a channel a group owner is barred from.</summary>
    [Theory]
    [InlineData(2412)]
    [InlineData(2437)]
    [InlineData(5180)]
    [InlineData(5260)]
    [InlineData(5500)]
    [InlineData(5700)]
    [InlineData(5745)]
    [InlineData(0)]
    public void No_rung_is_ever_illegal(int station)
    {
        foreach (var rung in GroupChannel.Ladder(station))
            Assert.True(GroupChannel.Allowed(rung), $"{rung}MHz is barred to a group owner");
    }

    /// <summary>No rung repeats — every retry is a genuinely different ask.</summary>
    [Theory]
    [InlineData(2437)]
    [InlineData(5180)]
    [InlineData(5500)]
    [InlineData(5745)]
    public void Every_rung_is_a_different_ask(int station)
    {
        var ladder = GroupChannel.Ladder(station);

        Assert.Equal(ladder.Length, ladder.Distinct().Count());
    }

    /// <summary>Each rung can be said out loud in terms of what it costs.</summary>
    [Fact]
    public void Each_rung_explains_itself()
    {
        Assert.Contains("nothing has to move", GroupChannel.Describe(5745, 5745), StringComparison.Ordinal);
        Assert.Contains("same band", GroupChannel.Describe(5745, 5180), StringComparison.Ordinal);
        Assert.Contains("cost them their Wi-Fi", GroupChannel.Describe(2437, 5180), StringComparison.Ordinal);
        Assert.Contains("leave your Wi-Fi", GroupChannel.Describe(GroupChannel.Anything, 5180), StringComparison.Ordinal);
    }
}
