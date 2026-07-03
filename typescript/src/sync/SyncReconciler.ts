/**
 * Deterministic last-write-wins reconciliation. Every device that receives the
 * same set of {@link SyncRecord}s — in any order, over any path — converges on
 * the identical winning record per item, with no server and no coordinator.
 *
 * Total order (later wins): createdAtMs, then logicalClock, then deviceId
 * (ordinal), then recordId bytes. The last two are arbitrary-but-stable
 * tie-breakers so genuinely concurrent writes still resolve the same way on
 * every device. The i64 fields are compared as BigInt to avoid precision loss.
 *
 * SPDX-License-Identifier: MIT
 */

import { SyncRecord, uuidToBytes } from "./SyncRecord.js";

/**
 * Orders two records: >0 if `a` wins, <0 if `b` wins, 0 only if they are the
 * same record.
 */
export function compareSyncRecords(a: SyncRecord, b: SyncRecord): number {
  let c = cmpBig(a.createdAtMs, b.createdAtMs);
  if (c !== 0) return c;
  c = cmpBig(a.logicalClock, b.logicalClock);
  if (c !== 0) return c;
  c = compareOrdinal(a.deviceId ?? "", b.deviceId ?? "");
  if (c !== 0) return c;
  return compareBytes(uuidToBytes(a.recordId), uuidToBytes(b.recordId));
}

/**
 * The winning record among `records` (all assumed to be for one item). Throws
 * if the sequence is empty.
 */
export function winner(records: Iterable<SyncRecord>): SyncRecord {
  let best: SyncRecord | null = null;
  for (const r of records) {
    if (best === null || compareSyncRecords(r, best) > 0) best = r;
  }
  if (best === null) throw new Error("No records to reconcile.");
  return best;
}

/**
 * Merges records into the winning record per {@link SyncRecord.itemId} — the
 * converged view of a device's local state.
 */
export function merge(records: Iterable<SyncRecord>): Map<string, SyncRecord> {
  const map = new Map<string, SyncRecord>();
  for (const r of records) {
    const key = r.itemId ?? "";
    const current = map.get(key);
    if (current === undefined || compareSyncRecords(r, current) > 0) {
      map.set(key, r);
    }
  }
  return map;
}

// ── Comparators ──────────────────────────────────────────────────────────────

function cmpBig(a: bigint, b: bigint): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Ordinal (code-unit) string comparison — matches C#'s string.CompareOrdinal,
 * which compares UTF-16 code units. JavaScript's `<`/`>` on strings does
 * exactly the same (lexicographic by UTF-16 code unit).
 */
function compareOrdinal(a: string, b: string): number {
  if (a === b) return 0;
  return a < b ? -1 : 1;
}

function compareBytes(a: Uint8Array, b: Uint8Array): number {
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) {
    if (a[i] !== b[i]) return a[i] < b[i] ? -1 : 1;
  }
  return a.length - b.length;
}
