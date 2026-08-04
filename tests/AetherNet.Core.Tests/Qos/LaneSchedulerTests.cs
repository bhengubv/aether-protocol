// SPDX-License-Identifier: MIT

using AetherNet.Qos;
using Xunit;

namespace AetherNet.Core.Tests.Qos;

/// <summary>
/// The lane scheduler's job: keep a bulk backlog from starving real-time traffic, never block emergency
/// or control, and share the rest of the link fairly by weight.
/// </summary>
public class LaneSchedulerTests
{
    private static LaneScheduler<string> New() => new();

    [Fact]
    public void EmptyScheduler_TryDequeue_ReturnsFalse()
    {
        var s = New();
        Assert.False(s.TryDequeue(out _, out _));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void EmergencyThenControl_AreServedStrictlyFirst()
    {
        var s = New();
        for (var i = 0; i < 5; i++) s.Enqueue($"bulk{i}", TrafficClass.Bulk, 1000);
        s.Enqueue("ctrl", TrafficClass.Control, 100);
        s.Enqueue("sos", TrafficClass.Emergency, 100);

        Assert.True(s.TryDequeue(out var first, out var c1));
        Assert.Equal("sos", first);
        Assert.Equal(TrafficClass.Emergency, c1);

        Assert.True(s.TryDequeue(out var second, out var c2));
        Assert.Equal("ctrl", second);
        Assert.Equal(TrafficClass.Control, c2);
    }

    [Fact]
    public void ItemsWithinALane_DequeueInFifoOrder()
    {
        var s = New();
        s.Enqueue("a", TrafficClass.Standard, 100);
        s.Enqueue("b", TrafficClass.Standard, 100);
        s.Enqueue("c", TrafficClass.Standard, 100);

        Assert.True(s.TryDequeue(out var x, out _)); Assert.Equal("a", x);
        Assert.True(s.TryDequeue(out var y, out _)); Assert.Equal("b", y);
        Assert.True(s.TryDequeue(out var z, out _)); Assert.Equal("c", z);
    }

    [Fact]
    public void RealtimePacket_IsNotStarvedByABulkBacklog()
    {
        var s = New();
        for (var i = 0; i < 500; i++) s.Enqueue($"bulk{i}", TrafficClass.Bulk, 1000);
        // Drain a couple so the round-robin pointer is parked mid-bulk...
        s.TryDequeue(out _, out _);
        s.TryDequeue(out _, out _);
        // ...then a single real-time packet arrives behind the 498-deep bulk queue.
        s.Enqueue("call", TrafficClass.Realtime, 200);

        var foundAt = -1;
        for (var i = 1; i <= 25; i++)
        {
            Assert.True(s.TryDequeue(out var item, out _));
            if (item == "call") { foundAt = i; break; }
        }

        Assert.InRange(foundAt, 1, 12); // bounded latency — nowhere near 500
    }

    [Fact]
    public void Bulk_StillMakesProgressUnderHeavyRealtimeLoad()
    {
        // Base quantum == packet size, so a round is short (≈ weight packets per lane) and the
        // interleaving is visible in a small window — bulk should surface roughly once per round.
        var s = new LaneScheduler<string>(realtimeWeight: 4, standardWeight: 2, bulkWeight: 1, baseQuantumBytes: 200);
        for (var i = 0; i < 100; i++) s.Enqueue($"rt{i}", TrafficClass.Realtime, 200);
        for (var i = 0; i < 100; i++) s.Enqueue($"bulk{i}", TrafficClass.Bulk, 200);

        var bulkServed = 0;
        for (var i = 0; i < 40; i++)
        {
            Assert.True(s.TryDequeue(out var item, out _));
            if (item.StartsWith("bulk")) bulkServed++;
        }

        // Weight 4 : 1 means ≈ 4 real-time per bulk, so ~8 bulk in 40 dequeues — never zero.
        Assert.True(bulkServed >= 5, $"bulk must not be starved by real-time (served {bulkServed})");
    }

    [Fact]
    public void WeightedThroughput_RealtimeGetsRoughlyItsWeightShareOverBulk()
    {
        var s = New(); // realtime weight 4, bulk weight 1
        for (var i = 0; i < 1000; i++) s.Enqueue($"rt{i}", TrafficClass.Realtime, 1000);
        for (var i = 0; i < 1000; i++) s.Enqueue($"bulk{i}", TrafficClass.Bulk, 1000);

        int rt = 0, bulk = 0;
        for (var i = 0; i < 200; i++)
        {
            Assert.True(s.TryDequeue(out var item, out _));
            if (item.StartsWith("rt")) rt++;
            else if (item.StartsWith("bulk")) bulk++;
        }

        // Equal packet sizes + 4:1 weights → real-time should get well over twice the throughput.
        Assert.True(rt > bulk * 2, $"expected realtime >> bulk, got rt={rt} bulk={bulk}");
    }

    [Fact]
    public void OversizedPacket_LargerThanAWholeRoundOfQuanta_IsStillServed()
    {
        var s = new LaneScheduler<string>(realtimeWeight: 1, standardWeight: 1, bulkWeight: 1, baseQuantumBytes: 500);
        s.Enqueue("huge", TrafficClass.Bulk, 100_000); // far larger than any single round's credit

        Assert.True(s.TryDequeue(out var item, out var c));
        Assert.Equal("huge", item);
        Assert.Equal(TrafficClass.Bulk, c);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void CountAndCountIn_TrackQueuedItems()
    {
        var s = New();
        s.Enqueue("a", TrafficClass.Realtime, 10);
        s.Enqueue("b", TrafficClass.Bulk, 10);
        s.Enqueue("c", TrafficClass.Bulk, 10);

        Assert.Equal(3, s.Count);
        Assert.Equal(1, s.CountIn(TrafficClass.Realtime));
        Assert.Equal(2, s.CountIn(TrafficClass.Bulk));
        Assert.Equal(0, s.CountIn(TrafficClass.Control));
    }
}
