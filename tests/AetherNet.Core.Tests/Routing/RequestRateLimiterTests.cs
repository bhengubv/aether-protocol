// SPDX-License-Identifier: MIT

using System;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Coverage for <see cref="RequestRateLimiter"/> — the relay flood cap. Verifies per-source
/// burst+refill, that a sybil swarm can't exceed the aggregate ceiling, and that the limiter's
/// own memory can't be exhausted (no bucket allocated once the relay is saturated; idle buckets
/// pruned). A deterministic injected clock controls time.
/// </summary>
public class RequestRateLimiterTests
{
    private sealed class Clock { public long NowMs; }

    private static RequestRateLimiter Build(Clock clock,
        int capacity = 3, double refillPerSec = 1.0,
        int aggregateCapacity = 1000, double aggregateRefillPerSec = 1000.0,
        int maxSources = 8192)
        => new(capacity, refillPerSec, aggregateCapacity, aggregateRefillPerSec, maxSources, () => clock.NowMs);

    [Fact]
    public void Allows_BurstUpToCapacity_ThenDrops()
    {
        var rl = Build(new Clock(), capacity: 3);
        Assert.True(rl.TryAcquire("a"));
        Assert.True(rl.TryAcquire("a"));
        Assert.True(rl.TryAcquire("a"));   // 3-request burst consumed
        Assert.False(rl.TryAcquire("a"));  // 4th in the same instant → dropped
    }

    [Fact]
    public void Refills_OverTime()
    {
        var clock = new Clock();
        var rl = Build(clock, capacity: 3, refillPerSec: 1.0); // 1 token/sec

        for (int i = 0; i < 3; i++) Assert.True(rl.TryAcquire("a"));
        Assert.False(rl.TryAcquire("a"));

        clock.NowMs += 1000;               // +1 second → +1 token
        Assert.True(rl.TryAcquire("a"));   // exactly one allowed back
        Assert.False(rl.TryAcquire("a"));
    }

    [Fact]
    public void PerSource_IsIsolated()
    {
        var rl = Build(new Clock(), capacity: 2);
        Assert.True(rl.TryAcquire("a"));
        Assert.True(rl.TryAcquire("a"));
        Assert.False(rl.TryAcquire("a"));  // 'a' drained
        Assert.True(rl.TryAcquire("b"));   // 'b' is unaffected — one node's flood can't starve another
        Assert.True(rl.TryAcquire("b"));
    }

    [Fact]
    public void Aggregate_CapsTheSwarm_AndBoundsBucketAllocation()
    {
        // Each source is well under its own cap (10), but the relay aggregate ceiling is only 5.
        var rl = Build(new Clock(), capacity: 10, aggregateCapacity: 5, aggregateRefillPerSec: 0.0001);

        int allowed = 0;
        for (int i = 0; i < 100; i++)
            if (rl.TryAcquire($"src-{i}")) allowed++;

        Assert.Equal(5, allowed); // the distributed swarm still can't exceed the relay ceiling
        Assert.True(rl.TrackedSourceCount <= 5,
            $"a saturated relay must not allocate a per-source bucket (got {rl.TrackedSourceCount}) — " +
            "else the limiter is itself a memory-exhaustion vector");
    }

    [Fact]
    public void Prune_ReclaimsIdleBuckets_BoundingMemory()
    {
        var clock = new Clock();
        var rl = Build(clock, capacity: 2, refillPerSec: 100.0, maxSources: 2);

        Assert.True(rl.TryAcquire("old")); // 'old' bucket created
        clock.NowMs += 1000;               // 'old' refills back to full → idle, indistinguishable from fresh

        Assert.True(rl.TryAcquire("n1"));
        Assert.True(rl.TryAcquire("n2"));  // pushes past maxSources → idle prune fires, drops full 'old'
        Assert.True(rl.TryAcquire("n3"));

        Assert.True(rl.TrackedSourceCount <= 3,
            $"idle (full) buckets must be pruned so the map stays bounded (got {rl.TrackedSourceCount})");
    }

    [Fact]
    public void Ctor_RejectsNonPositiveCapacity()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RequestRateLimiter(capacity: 0));

    [Fact]
    public void Ctor_RejectsNonPositiveRefill()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RequestRateLimiter(refillPerSec: 0));
}
