// SPDX-License-Identifier: MIT
namespace AetherNet.Map.Crdt;

/// <summary>
/// Last-write-wins register clocked by a <see cref="HybridLogicalClock"/>. The value with the greater
/// HLC wins; because the HLC total order is deterministic across nodes (down to the node-id tiebreak),
/// every node converges on the identical value regardless of the order deltas arrive in.
///
/// Used for a map feature's scalar attributes (name, hours, phone, an accessibility yes/no, a sensor
/// reading). Concurrent edits to <i>different</i> attributes each live in their own register, so they
/// all survive — the failure mode of the old whole-record last-write-wins.
/// </summary>
/// <typeparam name="T">The value type. May be nullable; a null value is a valid (cleared) state.</typeparam>
public readonly record struct LwwRegister<T>(T Value, HybridLogicalClock Clock)
{
    /// <summary>Merge two registers for the same field — the higher-clocked value wins (commutative,
    /// associative, idempotent).</summary>
    public LwwRegister<T> Merge(in LwwRegister<T> other) => other.Clock > Clock ? other : this;

    /// <summary>Return a register with <paramref name="value"/> at <paramref name="clock"/> if that clock
    /// is newer than this one; otherwise this register unchanged (a stale write is a no-op).</summary>
    public LwwRegister<T> Set(T value, HybridLogicalClock clock)
        => clock > Clock ? new LwwRegister<T>(value, clock) : this;
}
