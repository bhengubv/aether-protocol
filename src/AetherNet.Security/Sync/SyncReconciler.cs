// SPDX-License-Identifier: MIT

namespace AetherNet.Security.Sync;

/// <summary>
/// Deterministic last-write-wins reconciliation. Every device that receives the
/// same set of <see cref="SyncRecord"/>s — in any order, over any path — converges
/// on the identical winning record per item, with no server and no coordinator.
///
/// Total order (later wins): CreatedAtMs, then LogicalClock, then DeviceId
/// (ordinal), then RecordId bytes. The last two are arbitrary-but-stable
/// tie-breakers so genuinely concurrent writes still resolve the same way on
/// every device.
/// </summary>
public static class SyncReconciler
{
    /// <summary>
    /// Orders two records: &gt;0 if <paramref name="a"/> wins, &lt;0 if
    /// <paramref name="b"/> wins, 0 only if they are the same record.
    /// </summary>
    public static int Compare(SyncRecord a, SyncRecord b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var c = a.CreatedAtMs.CompareTo(b.CreatedAtMs);
        if (c != 0) return c;
        c = a.LogicalClock.CompareTo(b.LogicalClock);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.DeviceId ?? string.Empty, b.DeviceId ?? string.Empty);
        if (c != 0) return c;
        return CompareGuids(a.RecordId, b.RecordId);
    }

    /// <summary>
    /// The winning record among <paramref name="records"/> (all assumed to be for
    /// one item). Throws if the sequence is empty.
    /// </summary>
    public static SyncRecord Winner(IEnumerable<SyncRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        SyncRecord? best = null;
        foreach (var r in records)
            if (best is null || Compare(r, best) > 0)
                best = r;
        return best ?? throw new ArgumentException("No records to reconcile.", nameof(records));
    }

    /// <summary>
    /// Merges records into the winning record per <see cref="SyncRecord.ItemId"/>
    /// — the converged view of a device's local state.
    /// </summary>
    public static IReadOnlyDictionary<string, SyncRecord> Merge(IEnumerable<SyncRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var map = new Dictionary<string, SyncRecord>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            var key = r.ItemId ?? string.Empty;
            if (!map.TryGetValue(key, out var current) || Compare(r, current) > 0)
                map[key] = r;
        }
        return map;
    }

    private static int CompareGuids(Guid a, Guid b)
    {
        Span<byte> ba = stackalloc byte[16];
        Span<byte> bb = stackalloc byte[16];
        a.TryWriteBytes(ba, bigEndian: true, out _);
        b.TryWriteBytes(bb, bigEndian: true, out _);
        return ba.SequenceCompareTo(bb);
    }
}
