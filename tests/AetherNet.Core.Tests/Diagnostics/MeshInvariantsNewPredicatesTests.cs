// SPDX-License-Identifier: MIT

using AetherNet.Content.Diagnostics;
using Xunit;

namespace AetherNet.Core.Tests.Diagnostics;

/// <summary>
/// Tests for the 3 new MeshInvariants predicates promoted to AetherNet.Content
/// in v1.3.0 (closes 02_REMAINING_WORK.md §10 runtime predicate wires):
/// WatchTogetherBoundedLatency, OutboxBounded, ByzantineQuorumReached.
///
/// The other 5 predicates (DtnCustodyEventuallyTerminates, MultiDeviceSyncConverges,
/// ContentBitmapEventuallyComplete, ForgeIntegrity, StreamSequenceMonotonic)
/// are still exercised by the integration tests in aether-media's
/// MeshIntegrationTests.cs, now consuming them from AetherNet.Content.Diagnostics.
/// </summary>
public class MeshInvariantsNewPredicatesTests
{
    // ─── WatchTogetherBoundedLatency ────────────────────────────────────

    [Fact]
    public void WatchTogether_AllFollowersWithinTolerance_ReturnsTrue()
    {
        // Host at 10000ms; followers at 9950, 10000, 10050 — all within 100ms.
        var ok = MeshInvariants.WatchTogetherBoundedLatency(
            hostPositionMs: 10000,
            followerPositionsAfterRttCompensationMs: new long[] { 9950, 10000, 10050 },
            toleranceMs: 100);
        Assert.True(ok);
    }

    [Fact]
    public void WatchTogether_OneFollowerExceedsTolerance_ReturnsFalse()
    {
        // Host at 10000ms; one follower at 10250 — exceeds 100ms tolerance.
        var ok = MeshInvariants.WatchTogetherBoundedLatency(
            hostPositionMs: 10000,
            followerPositionsAfterRttCompensationMs: new long[] { 9990, 10010, 10250 },
            toleranceMs: 100);
        Assert.False(ok);
    }

    [Fact]
    public void WatchTogether_NegativeDriftRespected_AbsValue()
    {
        // Follower behind host by 150ms — also exceeds.
        var ok = MeshInvariants.WatchTogetherBoundedLatency(
            hostPositionMs: 5000,
            followerPositionsAfterRttCompensationMs: new long[] { 4850 },
            toleranceMs: 100);
        Assert.False(ok);
    }

    [Fact]
    public void WatchTogether_NoFollowers_ReturnsTrue()
    {
        var ok = MeshInvariants.WatchTogetherBoundedLatency(10000, Array.Empty<long>(), 100);
        Assert.True(ok);
    }

    [Fact]
    public void WatchTogether_NullFollowers_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MeshInvariants.WatchTogetherBoundedLatency(10000, null!, 100));
    }

    [Fact]
    public void WatchTogether_NegativeTolerance_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MeshInvariants.WatchTogetherBoundedLatency(10000, new long[] { 10000 }, -1));
    }

    // ─── OutboxBounded ──────────────────────────────────────────────────

    [Fact]
    public void Outbox_BelowCap_ReturnsTrue()
    {
        Assert.True(MeshInvariants.OutboxBounded(50, 100));
    }

    [Fact]
    public void Outbox_AtCap_ReturnsTrue()
    {
        Assert.True(MeshInvariants.OutboxBounded(100, 100));
    }

    [Fact]
    public void Outbox_OverCap_ReturnsFalse()
    {
        Assert.False(MeshInvariants.OutboxBounded(101, 100));
    }

    [Fact]
    public void Outbox_ZeroCap_OnlyEmptyQueueOk()
    {
        Assert.True(MeshInvariants.OutboxBounded(0, 0));
        Assert.False(MeshInvariants.OutboxBounded(1, 0));
    }

    [Fact]
    public void Outbox_NegativeDepth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MeshInvariants.OutboxBounded(-1, 100));
    }

    [Fact]
    public void Outbox_NegativeCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MeshInvariants.OutboxBounded(50, -1));
    }

    // ─── ByzantineQuorumReached ─────────────────────────────────────────

    [Fact]
    public void Byzantine_AllAgree_ReturnsTrue_DefaultTolerance()
    {
        // N=3, default f=N/3=1, threshold = N-f = 2. All 3 agree → quorum.
        var ok = MeshInvariants.ByzantineQuorumReached(
            new[] { "deadbeef", "deadbeef", "deadbeef" },
            out var agreed);
        Assert.True(ok);
        Assert.Equal("deadbeef", agreed);
    }

    [Fact]
    public void Byzantine_SupermajorityAgrees_ReturnsTrue()
    {
        // N=4, default f=N/3=1, threshold = N-f = 3. Three peers agree → quorum.
        var ok = MeshInvariants.ByzantineQuorumReached(
            new[] { "good", "good", "good", "evil" },
            out var agreed);
        Assert.True(ok);
        Assert.Equal("good", agreed);
    }

    [Fact]
    public void Byzantine_NoSupermajority_ReturnsFalse()
    {
        // N=4, default f=1, threshold=3. Only 2 agree → no quorum.
        var ok = MeshInvariants.ByzantineQuorumReached(
            new[] { "a", "a", "b", "b" },
            out var agreed);
        Assert.False(ok);
        // agreedValue holds modal value for diagnostics; either "a" or "b" is fine.
        Assert.Contains(agreed, new[] { "a", "b" });
    }

    [Fact]
    public void Byzantine_CustomFaultTolerance_Respected()
    {
        // N=5, f=2 → threshold=3. 3 peers agree → quorum.
        var ok = MeshInvariants.ByzantineQuorumReached(
            new[] { "x", "x", "x", "y", "z" },
            out var agreed,
            faultTolerance: 2);
        Assert.True(ok);
        Assert.Equal("x", agreed);
    }

    [Fact]
    public void Byzantine_CustomFaultTolerance_StricterRejection()
    {
        // N=5, f=0 → threshold=5. Need ALL to agree; only 4 do → no quorum.
        var ok = MeshInvariants.ByzantineQuorumReached(
            new[] { "x", "x", "x", "x", "y" },
            out var agreed,
            faultTolerance: 0);
        Assert.False(ok);
    }

    [Fact]
    public void Byzantine_EmptyVotes_ReturnsFalse()
    {
        var ok = MeshInvariants.ByzantineQuorumReached(
            Array.Empty<string>(),
            out var agreed);
        Assert.False(ok);
        Assert.Null(agreed);
    }

    [Fact]
    public void Byzantine_FaultToleranceMeetsN_ReturnsFalse()
    {
        // f=N means even all-agree fails (sanity bound).
        var ok = MeshInvariants.ByzantineQuorumReached(
            new[] { "x", "x", "x" },
            out var agreed,
            faultTolerance: 3);
        Assert.False(ok);
    }

    [Fact]
    public void Byzantine_NullVotes_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MeshInvariants.ByzantineQuorumReached<string>(null!, out _));
    }

    [Fact]
    public void Byzantine_StructuralEquality_GroupsByDefault()
    {
        // Records use structural equality by default — peers reporting the same
        // ContentDescriptor-shaped object should agree even if instances differ.
        var ok = MeshInvariants.ByzantineQuorumReached(
            new[]
            {
                ("artist-x", "track-y"),
                ("artist-x", "track-y"),
                ("artist-x", "track-y"),
            },
            out var agreed);
        Assert.True(ok);
        Assert.Equal(("artist-x", "track-y"), agreed);
    }
}
