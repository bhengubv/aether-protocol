// SPDX-License-Identifier: MIT

package aethernet.sync

/**
 * Deterministic last-write-wins reconciliation. Every device that receives the
 * same set of [SyncRecord]s — in any order, over any path — converges on the
 * identical winning record per item, with no server and no coordinator.
 *
 * Total order (later wins): createdAtMs, then logicalClock, then deviceId
 * (ordinal), then recordId bytes. The last two are arbitrary-but-stable
 * tie-breakers so genuinely concurrent writes still resolve the same way on
 * every device.
 */
object SyncReconciler {

    /**
     * Orders two records: >0 if [a] wins, <0 if [b] wins, 0 only if they are the
     * same record.
     *
     * deviceId uses ordinal (UTF-16 code-unit) comparison — Kotlin's
     * `String.compareTo` matches C#'s `string.CompareOrdinal`. The recordId
     * tie-break compares the 16 RFC-4122 big-endian bytes as unsigned, matching
     * the C# `Guid` big-endian byte compare.
     */
    fun compare(a: SyncRecord, b: SyncRecord): Int {
        var c = a.createdAtMs.compareTo(b.createdAtMs)
        if (c != 0) return c
        c = a.logicalClock.compareTo(b.logicalClock)
        if (c != 0) return c
        c = a.deviceId.compareTo(b.deviceId) // ordinal, == string.CompareOrdinal
        if (c != 0) return c
        return compareUnsigned(a.recordIdBytes, b.recordIdBytes)
    }

    /**
     * The winning record among [records] (all assumed to be for one item).
     * Throws if the collection is empty.
     */
    fun winner(records: Iterable<SyncRecord>): SyncRecord {
        var best: SyncRecord? = null
        for (r in records) {
            if (best == null || compare(r, best) > 0) best = r
        }
        return best ?: throw IllegalArgumentException("No records to reconcile.")
    }

    /**
     * Merges records into the winning record per [SyncRecord.itemId] — the
     * converged view of a device's local state.
     */
    fun merge(records: Iterable<SyncRecord>): Map<String, SyncRecord> {
        val map = LinkedHashMap<String, SyncRecord>()
        for (r in records) {
            val key = r.itemId
            val current = map[key]
            if (current == null || compare(r, current) > 0) map[key] = r
        }
        return map
    }

    /** Lexicographic unsigned comparison of two equal-purpose 16-byte arrays. */
    private fun compareUnsigned(a: ByteArray, b: ByteArray): Int {
        val n = minOf(a.size, b.size)
        for (i in 0 until n) {
            val d = (a[i].toInt() and 0xff) - (b[i].toInt() and 0xff)
            if (d != 0) return d
        }
        return a.size - b.size
    }
}
