// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Remembering the channel, so hosting does not cost the guest their internet.
///
/// <para>
/// Measured on two handsets: the giver raised a group, the phone that joined it lost the house network
/// entirely and stayed lost — which is the exact moment a stranger being helped decides they have been
/// broken rather than helped. The giver's own log said why: it asked what channel its Wi-Fi was on
/// while it was already group owner, got nothing, and put the group wherever the framework liked.
/// </para>
/// </summary>
public class StationChannelTests
{
    /// <summary>A real reading is used as-is.</summary>
    [Fact]
    public void A_live_channel_is_taken()
    {
        Assert.Equal(2437, new StationChannel().Best(2437));
    }

    /// <summary>
    /// <b>The whole point.</b> Nothing now, but something before — use what was true.
    /// </summary>
    [Fact]
    public void A_channel_seen_earlier_survives_the_station_going_down()
    {
        var channel = new StationChannel();

        channel.Best(5180);                       // while still associated
        Assert.Equal(5180, channel.Best(0));      // and now, hosting, with the station down
    }

    /// <summary>
    /// A disconnected Android says <c>-1</c>, which is not a channel and must not be banked.
    /// </summary>
    /// <remarks>
    /// Worth its own test because the platform read is clamped to zero elsewhere in this app, and a
    /// clamp turns a refusal into something that looks like an answer.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2399)]
    [InlineData(7201)]
    [InlineData(int.MaxValue)]
    public void Nonsense_is_never_remembered(int nonsense)
    {
        var channel = new StationChannel();

        Assert.Equal(0, channel.Best(nonsense));
        Assert.Equal(0, channel.Known);
    }

    /// <summary>And nonsense cannot displace something real.</summary>
    [Fact]
    public void Nonsense_cannot_overwrite_a_real_channel()
    {
        var channel = new StationChannel();

        channel.Best(2412);
        channel.Best(-1);

        Assert.Equal(2412, channel.Known);
    }

    /// <summary>
    /// The newest real answer wins, because a phone moves between networks all day.
    /// </summary>
    /// <remarks>
    /// A channel from somewhere else is worse than none: it aims the group at a channel nothing here
    /// is on, which is the failure this class exists to prevent, arrived at from the other direction.
    /// </remarks>
    [Fact]
    public void A_newer_channel_replaces_an_older_one()
    {
        var channel = new StationChannel();

        channel.Best(2412);
        Assert.Equal(5745, channel.Best(5745));
        Assert.Equal(5745, channel.Best(0));
    }

    /// <summary>Never having seen Wi-Fi is honestly reported as nothing.</summary>
    /// <remarks>
    /// Zero has to keep meaning "no preference" — the caller uses it to decide whether to ask for a
    /// channel at all, and a made-up default would aim the radio on a guess.
    /// </remarks>
    [Fact]
    public void Having_never_seen_wifi_is_admitted()
    {
        Assert.Equal(0, new StationChannel().Best(0));
    }

    /// <summary>The real bands are accepted at their edges.</summary>
    [Theory]
    [InlineData(2412)]   // 2.4GHz channel 1
    [InlineData(2484)]   // channel 14
    [InlineData(5180)]   // 5GHz channel 36
    [InlineData(5825)]   // channel 165
    [InlineData(5955)]   // 6GHz channel 1
    [InlineData(7115)]   // 6GHz channel 233
    public void Every_real_band_is_accepted(int frequency)
    {
        Assert.True(StationChannel.IsReal(frequency), $"{frequency}MHz is a real channel");
    }
}
