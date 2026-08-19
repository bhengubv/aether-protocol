// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A microphone produces fifty frames a second whatever the radio is doing, and between two handsets
/// BLE carries about a fifth of what this codec makes. Something has to give, and the only question
/// is whether it gives on purpose.
///
/// <para>
/// It used to give by accident: every frame was handed to its own fire-and-forget task, so the ones
/// that could not go out simply accumulated. On two phones that reached eight thread-pool threads and
/// a quarter of a million bytes of queued audio within five seconds, and Android killed both
/// processes mid-call — the call connected every time and died before a word could be heard.
/// </para>
///
/// <para>
/// These pin the queue that replaced it: fixed size, never blocks the capture thread, and when it is
/// full it discards the OLDEST frame. A late voice frame is worth less than the one behind it, so the
/// newest audio must always be the audio that survives.
/// </para>
/// </summary>
public class VoiceFrameBackpressureTests
{
    /// <summary>The same queue <c>CallService</c> builds for a call.</summary>
    private static Channel<(Guid CallId, byte[] Payload, uint Sequence)> Queue(int capacity = 16) =>
        Channel.CreateBounded<(Guid, byte[], uint)>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

    private static bool Offer(Channel<(Guid, byte[], uint)> q, uint sequence) =>
        q.Writer.TryWrite((Guid.Empty, new byte[80], sequence));

    // ── it must never grow ─────────────────────────────────────────────────

    /// <summary>
    /// Ten seconds of speech into a radio that never drains a single frame. Nothing may accumulate —
    /// this is the exact shape of the crash.
    /// </summary>
    [Theory]
    [InlineData(50)]      // one second
    [InlineData(250)]     // five seconds — where both phones died
    [InlineData(500)]     // ten seconds
    public void A_radio_that_never_drains_does_not_make_the_queue_grow(int frames)
    {
        var q = Queue();

        for (uint i = 0; i < frames; i++)
            Assert.True(Offer(q, i), "the capture thread was refused, which would stall the microphone");

        Assert.True(q.Reader.Count <= 16, $"the queue grew to {q.Reader.Count} frames");
    }

    /// <summary>Writing must never block, whatever the reader is doing — it runs on the capture thread.</summary>
    [Fact]
    public void Handing_over_a_frame_never_blocks_the_capture_thread()
    {
        var q = Queue();
        for (uint i = 0; i < 200; i++)
        {
            var accepted = Offer(q, i);
            Assert.True(accepted, $"frame {i} was refused rather than displacing an older one");
        }
    }

    // ── it must drop the right end ─────────────────────────────────────────

    /// <summary>
    /// What survives is the newest audio. Dropping the newest instead would leave a listener hearing
    /// a stale fragment while the live speech is thrown away.
    /// </summary>
    [Fact]
    public void The_frames_that_survive_are_the_newest_ones()
    {
        var q = Queue(capacity: 4);

        for (uint i = 0; i < 10; i++) Offer(q, i);

        var kept = new List<uint>();
        while (q.Reader.TryRead(out var frame)) kept.Add(frame.Item3);

        Assert.Equal(new uint[] { 6, 7, 8, 9 }, kept);
    }

    /// <summary>And in order — a jitter buffer can fill a gap, but not un-shuffle a stream.</summary>
    [Fact]
    public void Surviving_frames_stay_in_order()
    {
        var q = Queue(capacity: 8);
        for (uint i = 0; i < 40; i++) Offer(q, i);

        var kept = new List<uint>();
        while (q.Reader.TryRead(out var frame)) kept.Add(frame.Item3);

        Assert.Equal(kept.OrderBy(x => x), kept);
    }

    // ── it must keep up when the radio can ─────────────────────────────────

    /// <summary>On a radio that keeps pace, nothing is dropped at all.</summary>
    [Fact]
    public async Task A_radio_that_keeps_up_loses_nothing()
    {
        var q = Queue();
        var sent = new List<uint>();

        var pump = Task.Run(async () =>
        {
            await foreach (var frame in q.Reader.ReadAllAsync()) sent.Add(frame.Item3);
        });

        for (uint i = 0; i < 50; i++)
        {
            Offer(q, i);
            await Task.Delay(1);       // the reader drains between frames
        }

        q.Writer.TryComplete();
        await pump;

        Assert.Equal(50, sent.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (uint)i), sent);
    }

    /// <summary>
    /// Closing the queue ends the sender, so hanging up does not leave a task holding stale speech
    /// from a call that is over.
    /// </summary>
    [Fact]
    public async Task Ending_the_call_ends_the_sender()
    {
        var q = Queue();
        var pump = Task.Run(async () =>
        {
            await foreach (var _ in q.Reader.ReadAllAsync()) { }
        });

        for (uint i = 0; i < 5; i++) Offer(q, i);
        q.Writer.TryComplete();

        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.IsCompletedSuccessfully);
    }
}
