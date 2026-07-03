// SPDX-License-Identifier: MIT

import Foundation

/// Deterministic last-write-wins reconciliation. Every device that receives the
/// same set of ``SyncRecord``s — in any order, over any path — converges on the
/// identical winning record per item, with no server and no coordinator.
///
/// Total order (later wins): `createdAtMs`, then `logicalClock`, then `deviceId`
/// (ordinal), then `recordId` bytes. The last two are arbitrary-but-stable
/// tie-breakers so genuinely concurrent writes still resolve the same way on
/// every device.
///
/// Mirrors the C# `SyncReconciler` (`src/AetherNet.Security/Sync/`).
public enum SyncReconciler {
    /// Orders two records: > 0 if `a` wins, < 0 if `b` wins, 0 only if they are
    /// the same record.
    public static func compare(_ a: SyncRecord, _ b: SyncRecord) -> Int {
        if a.createdAtMs != b.createdAtMs {
            return a.createdAtMs < b.createdAtMs ? -1 : 1
        }
        if a.logicalClock != b.logicalClock {
            return a.logicalClock < b.logicalClock ? -1 : 1
        }
        let d = compareOrdinal(a.deviceId, b.deviceId)
        if d != 0 { return d }
        return compareBytes(a.recordId, b.recordId)
    }

    /// The winning record among `records` (all assumed to be for one item).
    /// Returns `nil` if the sequence is empty (the C# reference throws here; a
    /// nil return keeps the Swift API non-throwing — callers force-unwrap when a
    /// winner is guaranteed).
    public static func winner<S: Sequence>(_ records: S) -> SyncRecord? where S.Element == SyncRecord {
        var best: SyncRecord?
        for r in records {
            if best == nil || compare(r, best!) > 0 {
                best = r
            }
        }
        return best
    }

    /// Merges records into the winning record per ``SyncRecord/itemId`` — the
    /// converged view of a device's local state.
    public static func merge<S: Sequence>(_ records: S) -> [String: SyncRecord] where S.Element == SyncRecord {
        var map: [String: SyncRecord] = [:]
        for r in records {
            let key = r.itemId
            if let current = map[key] {
                if compare(r, current) > 0 { map[key] = r }
            } else {
                map[key] = r
            }
        }
        return map
    }

    // MARK: - tie-breakers

    /// Ordinal string comparison over UTF-16 code units, matching C#
    /// `string.CompareOrdinal`. Returns -1 / 0 / +1.
    private static func compareOrdinal(_ a: String, _ b: String) -> Int {
        var ia = a.utf16.makeIterator()
        var ib = b.utf16.makeIterator()
        while true {
            let ca = ia.next()
            let cb = ib.next()
            switch (ca, cb) {
            case (nil, nil): return 0
            case (nil, _): return -1
            case (_, nil): return 1
            case let (x?, y?):
                if x != y { return x < y ? -1 : 1 }
            }
        }
    }

    /// Lexicographic unsigned-byte comparison, matching C#'s big-endian
    /// `Guid` byte `SequenceCompareTo`. Returns -1 / 0 / +1.
    private static func compareBytes(_ a: [UInt8], _ b: [UInt8]) -> Int {
        let n = min(a.count, b.count)
        var i = 0
        while i < n {
            if a[i] != b[i] { return a[i] < b[i] ? -1 : 1 }
            i += 1
        }
        if a.count != b.count { return a.count < b.count ? -1 : 1 }
        return 0
    }
}
