// SPDX-License-Identifier: MIT

using AetherNet.Voice;
using AetherNet.Voice.Models;
using Xunit;

namespace AetherNet.Core.Tests;

public class JitterBufferTests
{
    private static VoiceFrame Frame(uint sequence, long timestampMs = 0, byte[]? payload = null)
        => new()
        {
            CallId = Guid.Empty,
            SenderUhid = "alice",
            Sequence = sequence,
            TimestampMs = timestampMs == 0 ? sequence * 20L : timestampMs,
            EncodedPayload = payload ?? new byte[] { (byte)sequence },
            IsSilence = false,
        };

    // ── Construction ────────────────────────────────────────────────

    [Fact]
    public void Ctor_RejectsNonPositiveTargetDepth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JitterBuffer(targetDepthMs: 0));
    }

    [Fact]
    public void Ctor_RejectsMaxLessThanTarget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JitterBuffer(targetDepthMs: 100, maxDepthMs: 50));
    }

    [Fact]
    public void Push_NullFrame_Throws()
    {
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 200);
        Assert.Throws<ArgumentNullException>(() => buf.Push(null!));
    }

    // ── In-order ────────────────────────────────────────────────────

    [Fact]
    public void Push_InOrder_PopReturnsInOrder()
    {
        // Use a tiny target so Pop can return immediately after the wait.
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 1000);

        buf.Push(Frame(1));
        buf.Push(Frame(2));
        buf.Push(Frame(3));
        Assert.Equal(3, buf.Count);

        // Wait past target depth so Pop is permitted.
        Thread.Sleep(30);

        Assert.Equal(1u, buf.Pop()!.Sequence);
        Assert.Equal(2u, buf.Pop()!.Sequence);
        Assert.Equal(3u, buf.Pop()!.Sequence);
        Assert.Null(buf.Pop());
        Assert.Equal(3u, buf.LastPopped);
    }

    // ── Out-of-order ────────────────────────────────────────────────

    [Fact]
    public void Push_OutOfOrder_PopReorders()
    {
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 1000);

        buf.Push(Frame(3));
        buf.Push(Frame(1));
        buf.Push(Frame(2));

        Thread.Sleep(30);

        Assert.Equal(1u, buf.Pop()!.Sequence);
        Assert.Equal(2u, buf.Pop()!.Sequence);
        Assert.Equal(3u, buf.Pop()!.Sequence);
    }

    [Fact]
    public void Push_DuplicateSequence_LastWriteWins_StillSingleFrame()
    {
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 1000);

        buf.Push(Frame(1, payload: new byte[] { 0xAA }));
        buf.Push(Frame(1, payload: new byte[] { 0xBB }));

        Assert.Equal(1, buf.Count);

        Thread.Sleep(30);
        var popped = buf.Pop();
        Assert.NotNull(popped);
        // The SortedDictionary indexer-overwrite means the second push replaced the first.
        Assert.Equal(0xBB, popped!.EncodedPayload[0]);
        Assert.Null(buf.Pop());
    }

    // ── Late frames ─────────────────────────────────────────────────

    [Fact]
    public void Push_LateFrameAfterPop_IsDiscarded()
    {
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 1000);

        buf.Push(Frame(5));
        Thread.Sleep(30);
        var first = buf.Pop();
        Assert.NotNull(first);
        Assert.Equal(5u, first!.Sequence);

        // 4 is older than next-expected (6) — should be dropped silently.
        buf.Push(Frame(4));
        Assert.Equal(0, buf.Count);

        // 6 is the next expected and should be admitted.
        buf.Push(Frame(6));
        Assert.Equal(1, buf.Count);
    }

    // ── Empty buffer ────────────────────────────────────────────────

    [Fact]
    public void Pop_EmptyBuffer_ReturnsNull()
    {
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 200);
        Assert.Null(buf.Pop());
        Assert.Null(buf.LastPopped);
    }

    [Fact]
    public void Pop_BeforeTargetDepth_ReturnsNull()
    {
        // Use a healthy target so there's no race with the test thread.
        var buf = new JitterBuffer(targetDepthMs: 200, maxDepthMs: 1000);
        buf.Push(Frame(1));

        // Don't wait — we should be well below the 200ms target.
        Assert.Null(buf.Pop());
        Assert.Equal(1, buf.Count);
    }

    // ── Max depth eviction ──────────────────────────────────────────

    [Fact]
    public void Push_BeyondMaxDepth_DropsOldestFrames()
    {
        // Max=50ms, frames at 0ms, 20ms, 40ms, 60ms, 80ms, 100ms — should evict the oldest
        // until depth <= 50ms.
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 50);

        for (uint seq = 1; seq <= 6; seq++)
            buf.Push(Frame(seq, timestampMs: (seq - 1) * 20L));

        // Last timestamp = 100ms, max=50ms, so frames with ts<50 (i.e. seq 1, 2) should be gone.
        Thread.Sleep(20);

        var first = buf.Pop();
        Assert.NotNull(first);
        Assert.True(first!.Sequence >= 3,
            $"expected oldest frames to have been evicted but Pop returned seq={first.Sequence}");
    }

    // ── Burst delivery ──────────────────────────────────────────────

    [Fact]
    public void Push_BurstDelivery_StillReturnsInOrder()
    {
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 5000);

        // Burst all 20 frames in a tight loop, deliberately shuffled.
        var sequences = new uint[] { 5, 1, 9, 3, 7, 2, 8, 6, 4, 10, 15, 12, 18, 11, 19, 14, 20, 13, 17, 16 };
        foreach (var s in sequences)
            buf.Push(Frame(s, timestampMs: s * 20L));

        Assert.Equal(20, buf.Count);
        Thread.Sleep(20);

        for (uint expected = 1; expected <= 20; expected++)
        {
            var f = buf.Pop();
            Assert.NotNull(f);
            Assert.Equal(expected, f!.Sequence);
        }
        Assert.Null(buf.Pop());
    }

    // ── Clear ───────────────────────────────────────────────────────

    [Fact]
    public void Clear_ResetsBufferAndState()
    {
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 200);
        buf.Push(Frame(1));
        buf.Push(Frame(2));
        Thread.Sleep(20);
        _ = buf.Pop();

        buf.Clear();

        Assert.Equal(0, buf.Count);
        Assert.Null(buf.LastPopped);
        Assert.Null(buf.Pop());

        // After clear, a "late" sequence relative to the previous expected must be
        // accepted again: cleared state means no next-expected gate.
        buf.Push(Frame(0));
        Assert.Equal(1, buf.Count);
    }

    // ── Sequence wraparound ─────────────────────────────────────────

    [Fact]
    public void Push_SequenceNearUInt32Wrap_OrderingTreatedCircularly()
    {
        // After popping a high sequence, a "wrapped" lower-but-newer sequence should be admitted.
        var buf = new JitterBuffer(targetDepthMs: 10, maxDepthMs: 1000);

        buf.Push(Frame(uint.MaxValue, timestampMs: 0));
        Thread.Sleep(20);
        var popped = buf.Pop();
        Assert.NotNull(popped);
        Assert.Equal(uint.MaxValue, popped!.Sequence);

        // Sequence 0 is "after" uint.MaxValue under circular comparison, so it should be admitted.
        buf.Push(Frame(0, timestampMs: 20));
        Assert.Equal(1, buf.Count);
    }
}
