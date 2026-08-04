// SPDX-License-Identifier: MIT
namespace AetherNet.Map.Crdt;

/// <summary>
/// A Hybrid Logical Clock (HLC): a compact, fixed-size causal timestamp = wall-clock milliseconds +
/// a per-millisecond counter + the originating node id. It gives causal ordering when messages flow
/// and graceful wall-clock last-write-wins under pure concurrency, in a fixed 10-plus-id bytes on the
/// wire — unlike a vector clock, whose size grows with the number of distinct authors (fatal for a
/// popular POI edited by hundreds of people on a 2 GB-RAM phone).
///
/// <para>This is the principled generalization of the existing <c>SyncRecord (CreatedAtMs, LogicalClock)</c>
/// pair plus a defined update rule; the total order below matches <c>SyncReconciler</c>'s tie-break
/// discipline (time, then counter, then node id).</para>
///
/// Wire layout (fixture-pinned): <c>physical_ms (i64 LE) · counter (u16 LE) · node_id (u16 len + UTF-8)</c>.
/// The counter is a <see cref="ushort"/>; on the rare overflow within a single millisecond the physical
/// component is advanced by 1 ms and the counter reset, preserving monotonicity without silent wrap.
/// </summary>
public readonly record struct HybridLogicalClock(long PhysicalMs, ushort Counter, string NodeId)
    : IComparable<HybridLogicalClock>
{
    /// <summary>The zero clock — sorts before any real timestamp. Useful as an initial/unset value.</summary>
    public static HybridLogicalClock Zero => new(0, 0, string.Empty);

    /// <summary>An initial clock for <paramref name="nodeId"/> at wall-clock <paramref name="nowMs"/>.</summary>
    public static HybridLogicalClock Start(string nodeId, long nowMs)
        => Normalize(Math.Max(0, nowMs), 0, nodeId);

    /// <summary>
    /// Advance this clock for a local event at wall-clock <paramref name="nowMs"/> (send / mutate).
    /// Standard HLC send rule: physical = max(local.physical, now); counter increments only when the
    /// physical component did not advance.
    /// </summary>
    public HybridLogicalClock Tick(long nowMs)
    {
        long pt = Math.Max(PhysicalMs, nowMs);
        int counter = pt == PhysicalMs ? Counter + 1 : 0;
        return Normalize(pt, counter, NodeId);
    }

    /// <summary>
    /// Merge a received remote clock into this local clock at wall-clock <paramref name="nowMs"/>
    /// (receive rule), producing this node's next clock. Deterministic and monotonic.
    /// </summary>
    public HybridLogicalClock Receive(HybridLogicalClock remote, long nowMs)
    {
        long pt = Math.Max(Math.Max(PhysicalMs, remote.PhysicalMs), nowMs);
        int counter;
        if (pt == PhysicalMs && pt == remote.PhysicalMs) counter = Math.Max(Counter, remote.Counter) + 1;
        else if (pt == PhysicalMs) counter = Counter + 1;
        else if (pt == remote.PhysicalMs) counter = remote.Counter + 1;
        else counter = 0;
        return Normalize(pt, counter, NodeId);
    }

    private static HybridLogicalClock Normalize(long physicalMs, int counter, string nodeId)
    {
        // Carry counter overflow into physical time rather than wrapping the u16 (no silent reset).
        while (counter > ushort.MaxValue)
        {
            physicalMs += 1;
            counter -= ushort.MaxValue + 1;
        }
        return new HybridLogicalClock(physicalMs, (ushort)counter, nodeId ?? string.Empty);
    }

    /// <summary>
    /// Total order: physical ms, then counter, then node id (ordinal). Deterministic across all nodes,
    /// so genuinely-concurrent writes resolve to the same winner everywhere — the LWW guarantee.
    /// </summary>
    public int CompareTo(HybridLogicalClock other)
    {
        int c = PhysicalMs.CompareTo(other.PhysicalMs);
        if (c != 0) return c;
        c = Counter.CompareTo(other.Counter);
        if (c != 0) return c;
        return string.CompareOrdinal(NodeId, other.NodeId);
    }

    public static bool operator <(HybridLogicalClock a, HybridLogicalClock b) => a.CompareTo(b) < 0;
    public static bool operator >(HybridLogicalClock a, HybridLogicalClock b) => a.CompareTo(b) > 0;
    public static bool operator <=(HybridLogicalClock a, HybridLogicalClock b) => a.CompareTo(b) <= 0;
    public static bool operator >=(HybridLogicalClock a, HybridLogicalClock b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{PhysicalMs}:{Counter}:{NodeId}";
}
