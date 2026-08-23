// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The queueing rules a link runs on, and the framing it speaks.
///
/// <para>
/// These were learned over several days of two phones failing in ways that looked exactly like a
/// flaky radio and were not — a call breaking up whenever anything transferred, a link tearing itself
/// down on every attachment, and a receiver being fed video continuously while drawing none of it.
/// Every one of those was a queueing decision, and none of them was testable while the rules lived
/// beside a radio.
/// </para>
/// </summary>
public class LinkPlumbingTests
{
    private static MeshLink Link() => new(new TcpClient(), null!);

    private static byte[] Frame(int n) => new byte[n];

    // ── Order: speech first, always ──────────────────────────────────────────

    [Fact]
    public void Speech_leaves_before_anything_that_was_queued_earlier()
    {
        var link = Link();

        // Queued in the worst possible order: a file, then a picture, then a syllable.
        link.Enqueue([1], SendLane.Bulk);
        link.Enqueue([2], SendLane.Interactive);
        link.Enqueue([3], SendLane.Video);
        link.Enqueue([4], SendLane.RealTime);

        Assert.Equal([4], link.NextFrame());       // speech
        Assert.Equal([3], link.NextFrame());       // then the picture
        Assert.Equal([2], link.NextFrame());       // then what somebody is waiting on
        Assert.Equal([1], link.NextFrame());       // then the file
        Assert.Null(link.NextFrame());
    }

    [Fact]
    public void A_file_never_holds_the_wire_while_a_call_is_up()
    {
        var link = Link();

        // A 36KB attachment chunk once held the wire for about a third of a second while every voice
        // frame offered during it waited behind it. No bitrate was low enough to fix that; the problem
        // was the order.
        for (var i = 0; i < 64; i++) link.Enqueue(Frame(36 * 1024), SendLane.Bulk);
        link.Enqueue([9], SendLane.RealTime);

        Assert.Equal([9], link.NextFrame());
    }

    // ── Bounds: what may be thrown away, and what may never be ───────────────

    [Fact]
    public void Speech_is_never_thrown_away()
    {
        var link = Link();

        // The real-time lane is unbounded on purpose: voice frames are 149 to 391 bytes, fifty a
        // second, and there is nothing to gain by discarding a syllable somebody is waiting to hear.
        for (var i = 0; i < 5000; i++)
            Assert.Equal(0, link.Enqueue([(byte)i], SendLane.RealTime));

        var kept = 0;
        while (link.NextFrame() is not null) kept++;
        Assert.Equal(5000, kept);
    }

    [Fact]
    public void What_somebody_is_waiting_on_is_never_thrown_away()
    {
        var link = Link();
        for (var i = 0; i < 500; i++)
            Assert.Equal(0, link.Enqueue([(byte)i], SendLane.Interactive));
    }

    [Fact]
    public void A_file_backing_up_drops_its_oldest_and_keeps_going()
    {
        var link = Link();

        // Attachments resume, so a dropped chunk is asked for again. An unbounded queue on a slow link
        // is a phone running out of memory instead.
        var dropped = 0;
        for (var i = 0; i < MeshLink.BulkDepth + 20; i++)
            dropped += link.Enqueue(Frame(1024), SendLane.Bulk);

        Assert.Equal(20, dropped);

        var waiting = 0;
        while (link.NextFrame() is not null) waiting++;
        Assert.Equal(MeshLink.BulkDepth, waiting);
    }

    // ── The expensive one ────────────────────────────────────────────────────

    [Fact]
    public void Video_backing_up_clears_the_whole_lane_rather_than_dropping_one_frame()
    {
        var link = Link();

        // This is the rule that cost the most to find. H.264 without temporal layering is a chain:
        // every P-frame decodes against the one before it. Drop the oldest and keep the rest and the
        // far side's decoder accepts every subsequent frame and produces NOTHING from them —
        // silently, with no error, until the next keyframe. Measured over a three-minute call: frames
        // arriving fell 11.6/s to 4/s while frames DRAWN fell 10.8/s to zero, decodeErrors 0
        // throughout.
        var dropped = 0;
        for (var i = 0; i < MeshLink.VideoDepth + 1; i++)
            dropped += link.Enqueue(Frame(4500), SendLane.Video);

        // Everything, not one. A chain with a link missing stays broken; an empty lane recovers on the
        // next keyframe, about a second away.
        Assert.Equal(MeshLink.VideoDepth + 1, dropped);
        Assert.Null(link.NextFrame());
    }

    [Fact]
    public void Video_under_the_bound_is_left_completely_alone()
    {
        var link = Link();

        // The slack has to be real slack. Clearing the lane at the slightest queueing would throw away
        // a picture every time the radio hiccupped.
        for (var i = 0; i < MeshLink.VideoDepth; i++)
            Assert.Equal(0, link.Enqueue(Frame(4500), SendLane.Video));

        var waiting = 0;
        while (link.NextFrame() is not null) waiting++;
        Assert.Equal(MeshLink.VideoDepth, waiting);
    }

    [Fact]
    public void Clearing_the_picture_never_touches_the_voice()
    {
        var link = Link();

        // The whole reason video has its own lane: the picture gives way first so the voice does not
        // have to. If a video overflow could take a syllable with it, the lane split bought nothing.
        for (var i = 0; i < 20; i++) link.Enqueue([(byte)i], SendLane.RealTime);
        for (var i = 0; i < MeshLink.VideoDepth + 5; i++) link.Enqueue(Frame(4500), SendLane.Video);

        var speech = 0;
        while (link.NextFrame() is { } f && f.Length == 1) speech++;
        Assert.Equal(20, speech);
    }

    [Fact]
    public void The_slack_is_under_half_a_second_of_video()
    {
        // Six frames at fifteen a second. Enough to ride out a brief stall; far too little to
        // accumulate the delay that made a call feel like a recording.
        Assert.True(MeshLink.VideoDepth / 15.0 < 0.5,
            $"{MeshLink.VideoDepth} frames is {MeshLink.VideoDepth / 15.0:0.00}s of slack");
    }

    // ── Framing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_frame_survives_the_wire_whole()
    {
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            var payload = new byte[9000];
            Random.Shared.NextBytes(payload);

            await Framing.WriteFrameAsync(a.GetStream(), payload);
            Assert.Equal(payload, await Framing.ReadFrameAsync(b.GetStream()));
        }
    }

    [Fact]
    public async Task Frames_keep_their_boundaries_back_to_back()
    {
        // TCP is a stream, not a sequence of messages. Two frames written together arrive as one lump
        // of bytes, and a reader that assumes otherwise reads a length out of the middle of somebody
        // else's payload — which is what "Broken pipe on a healthy socket" turned out to be.
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            byte[][] sent = [[1], new byte[300], [2, 3, 4], new byte[70000]];
            foreach (var f in sent) await Framing.WriteFrameAsync(a.GetStream(), f);

            foreach (var f in sent)
                Assert.Equal(f.Length, (await Framing.ReadFrameAsync(b.GetStream()))!.Length);
        }
    }

    [Fact]
    public async Task A_peer_that_goes_away_reads_as_gone_rather_than_hanging()
    {
        var (a, b) = await PairAsync();
        using (b)
        {
            a.Dispose();
            Assert.Null(await Framing.ReadFrameAsync(b.GetStream()));
        }
    }

    [Fact]
    public async Task A_length_that_is_not_a_length_closes_the_link()
    {
        // The first four bytes off a socket are the last thing that should ever be trusted: without a
        // ceiling they allocate whatever an unknown sender says.
        foreach (var claimed in new[] { -1, 0, Framing.MaxFrame + 1, int.MaxValue })
        {
            var (a, b) = await PairAsync();
            using (a) using (b)
            {
                await a.GetStream().WriteAsync(BitConverter.GetBytes(claimed));
                await a.GetStream().FlushAsync();
                Assert.Null(await Framing.ReadFrameAsync(b.GetStream()));
            }
        }
    }

    [Fact]
    public async Task Tuning_a_live_socket_succeeds_and_a_dead_one_is_reported()
    {
        var (a, b) = await PairAsync();
        using (b)
        {
            Assert.True(Framing.Tighten(a));
            Assert.True(a.NoDelay);
            Assert.Equal(16 * 1024, a.SendBufferSize);

            a.Dispose();
            Assert.False(Framing.Tighten(a));
        }
    }

    private static async Task<(TcpClient, TcpClient)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accepting = listener.AcceptTcpClientAsync();
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            return (client, await accepting);
        }
        finally { listener.Stop(); }
    }
}
