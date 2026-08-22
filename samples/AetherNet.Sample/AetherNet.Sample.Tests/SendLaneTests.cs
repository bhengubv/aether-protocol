// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Which queue a packet waits in, and which queues are allowed to throw things away.
///
/// <para>
/// Video shared the real-time lane with voice, and that lane is deliberately unbounded — the
/// reasoning being that real-time traffic is "small and rare enough" that a bound would only discard
/// something somebody was waiting for. That is true of speech: 149 to 391 bytes, fifty a second. It is
/// not true of video: 3.6 to 4.8 kilobytes, twenty a second, both directions at once. An unbounded
/// queue of those on a link that cannot carry them drops nothing and delivers everything late, with
/// the delay growing for the length of the call.
/// </para>
/// </summary>
public class SendLaneTests
{
    /// <summary>Speech first, always. Nothing outranks it.</summary>
    [Theory]
    [InlineData(PacketType.VoiceCall)]
    [InlineData(PacketType.VoicePtt)]
    public void Speech_travels_in_the_lane_that_yields_to_nothing(PacketType type)
        => Assert.Equal(SendLane.RealTime, PacketPriority.Lane(type));

    /// <summary>And it is never thrown away, because it is small enough never to need to be.</summary>
    [Fact]
    public void Speech_is_never_dropped()
        => Assert.False(PacketPriority.MayDropOldest(SendLane.RealTime));

    /// <summary>Pictures get their own lane — just below speech, and above everything else.</summary>
    [Theory]
    [InlineData(PacketType.VideoFrame)]
    [InlineData(PacketType.ScreenShare)]
    [InlineData(PacketType.StreamSegment)]
    public void Pictures_travel_below_speech_and_above_everything_else(PacketType type)
    {
        var lane = PacketPriority.Lane(type);

        Assert.Equal(SendLane.Video, lane);
        Assert.True(lane > SendLane.RealTime, "a picture must never delay a syllable");
        Assert.True(lane < SendLane.Interactive, "but it is still real time, and beats a receipt");
    }

    /// <summary>
    /// The whole point of the split: video can be dropped to stay current, and dropping it costs
    /// nothing, because a frame whose moment has passed is discarded by the far side anyway.
    /// </summary>
    [Fact]
    public void Pictures_may_be_dropped_to_stay_current()
        => Assert.True(PacketPriority.MayDropOldest(SendLane.Video));

    /// <summary>
    /// Setting a call up keeps the highest lane. A key or an answer arriving late is a call that never
    /// starts, and these are a handful of bytes — there is nothing to gain by moving them down.
    /// </summary>
    [Theory]
    [InlineData(PacketType.VoiceSignaling)]
    [InlineData(PacketType.VideoSignaling)]
    [InlineData(PacketType.GroupVideoSignaling)]
    public void Setting_a_call_up_is_worth_as_much_as_the_call(PacketType type)
        => Assert.Equal(SendLane.RealTime, PacketPriority.Lane(type));

    /// <summary>
    /// The camera going on or off must never be droppable, and it is not video however much it looks
    /// like it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was sent as <see cref="PacketType.VideoFrame"/>, on the reasoning that the type put it in
    /// the real-time lane — true when it was written, and silently falsified the moment video was
    /// given a lane of its own. VideoFrame then meant a six-deep drop-oldest queue: correct for
    /// pictures, catastrophic for this.
    /// </para>
    /// <para>
    /// A dropped camera-off leaves the far end showing a frozen last frame for the rest of the call,
    /// which is the exact failure this packet exists to prevent — and it would be dropped precisely
    /// when the link is busy, which is precisely when a camera is on.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_camera_going_off_can_never_be_dropped()
    {
        var control = PacketPriority.Lane(PacketType.VideoSignaling);
        var media = PacketPriority.Lane(PacketType.VideoFrame);

        Assert.NotEqual(media, control);
        Assert.False(PacketPriority.MayDropOldest(control),
            "the far end would sit on a frozen frame for the rest of the call");
        Assert.True(PacketPriority.MayDropOldest(media));
    }

    [Theory]
    [InlineData(PacketType.ChunkData)]
    [InlineData(PacketType.ChunkRequest)]
    [InlineData(PacketType.TorrentMetadata)]
    public void Files_get_whatever_is_left(PacketType type)
        => Assert.Equal(SendLane.Bulk, PacketPriority.Lane(type));

    [Fact]
    public void A_chat_message_is_neither_media_nor_a_file()
        => Assert.Equal(SendLane.Interactive, PacketPriority.Lane(PacketType.Data));

    /// <summary>
    /// Only the two lanes whose contents survive being dropped may drop. A chunk is asked for again
    /// and a frame is obsolete; a message and a syllable are neither.
    /// </summary>
    [Fact]
    public void Only_what_survives_being_dropped_is_droppable()
    {
        Assert.True(PacketPriority.MayDropOldest(SendLane.Bulk));
        Assert.True(PacketPriority.MayDropOldest(SendLane.Video));
        Assert.False(PacketPriority.MayDropOldest(SendLane.RealTime));
        Assert.False(PacketPriority.MayDropOldest(SendLane.Interactive));
    }

    /// <summary>
    /// The lane count has to match the enum, because it sizes the array of queues — one short and the
    /// lowest lane indexes past the end of it.
    /// </summary>
    [Fact]
    public void There_is_a_queue_for_every_lane()
    {
        Assert.Equal(PacketPriority.Lanes, Enum.GetValues<SendLane>().Length);

        foreach (var lane in Enum.GetValues<SendLane>())
            Assert.InRange((int)lane, 0, PacketPriority.Lanes - 1);
    }

    /// <summary>Every packet type lands in a real lane — nothing falls through the switch.</summary>
    [Fact]
    public void Every_kind_of_packet_has_somewhere_to_wait()
    {
        foreach (var type in Enum.GetValues<PacketType>())
            Assert.InRange((int)PacketPriority.Lane(type), 0, PacketPriority.Lanes - 1);
    }
}
