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
    [Fact]
    public void The_ladder_is_ordered_by_what_it_costs_the_other_phone()
    {
        var ladder = GroupChannel.Ladder(5180);

        Assert.Equal(5180, ladder[0]);                                  // nothing moves
        Assert.True(ladder[1] > GroupChannel.LowBandTo, "same band");    // a channel change
        Assert.True(ladder[2] < GroupChannel.LowBandTo, "other band");   // their network
        Assert.Equal(GroupChannel.Anything, ladder[^1]);                 // last resort
    }

    /// <summary>
    /// It always ends by accepting whatever the radio gives.
    /// </summary>
    /// <remarks>
    /// A group on a channel we did not choose still beats no group. Somebody has pressed a button and
    /// is holding two phones together; something has to happen.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2437)]
    [InlineData(5500)]
    public void The_ladder_always_ends_by_taking_what_it_is_given(int station)
    {
        Assert.Equal(GroupChannel.Anything, GroupChannel.Ladder(station)[^1]);
    }

    /// <summary>With no Wi-Fi at all, it does not pretend to know better.</summary>
    [Fact]
    public void With_no_wifi_it_asks_for_something_universally_legal()
    {
        var ladder = GroupChannel.Ladder(0);

        Assert.Equal(GroupChannel.LowFallback, ladder[0]);
        Assert.True(GroupChannel.Allowed(ladder[0]));
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
            if (rung != GroupChannel.Anything)
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
