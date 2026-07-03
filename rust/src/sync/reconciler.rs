// SPDX-License-Identifier: MIT

//! Deterministic last-write-wins reconciliation. Every device that receives the
//! same set of [`SyncRecord`]s — in any order, over any path — converges on the
//! identical winning record per item, with no server and no coordinator.
//!
//! Total order (later wins): `created_at_ms`, then `logical_clock`, then
//! `device_id` (byte-ordinal), then `record_id` bytes. The last two are
//! arbitrary-but-stable tie-breakers so genuinely concurrent writes still
//! resolve the same way on every device.
//!
//! The C# reference tie-breaks `device_id` with `string.CompareOrdinal`
//! (UTF-16 code-unit order). For device ids in the Basic Multilingual Plane —
//! and for all ASCII ids, including every fixture — that is identical to Rust's
//! UTF-8 byte order used here.

use std::cmp::Ordering;
use std::collections::HashMap;

use super::record::SyncRecord;

/// Orders two records under the last-write-wins total order.
///
/// Returns [`Ordering::Greater`] if `a` wins over `b`, [`Ordering::Less`] if `b`
/// wins, and [`Ordering::Equal`] only if they are the same record. This mirrors
/// the C# `SyncReconciler.Compare` (`> 0` ⇒ `a` wins).
pub fn compare(a: &SyncRecord, b: &SyncRecord) -> Ordering {
    a.created_at_ms
        .cmp(&b.created_at_ms)
        .then_with(|| a.logical_clock.cmp(&b.logical_clock))
        .then_with(|| a.device_id.as_bytes().cmp(b.device_id.as_bytes()))
        .then_with(|| a.record_id.cmp(&b.record_id))
}

/// The winning record among `records` (all assumed to be for one item).
///
/// Returns `None` if the slice is empty (the C# reference throws; a Rust
/// `Option` is the idiomatic equivalent). On ties the total order in [`compare`]
/// still yields a single deterministic winner.
pub fn winner(records: &[SyncRecord]) -> Option<&SyncRecord> {
    let mut best: Option<&SyncRecord> = None;
    for r in records {
        match best {
            // `compare(r, b) == Greater` ⇒ r wins ⇒ replace, matching C#'s
            // `Compare(r, best) > 0`.
            Some(b) if compare(r, b) != Ordering::Greater => {}
            _ => best = Some(r),
        }
    }
    best
}

/// Merges records into the winning record per `item_id` — the converged view of
/// a device's local state.
pub fn merge(records: &[SyncRecord]) -> HashMap<String, SyncRecord> {
    let mut map: HashMap<String, SyncRecord> = HashMap::new();
    for r in records {
        match map.get(&r.item_id) {
            Some(current) if compare(r, current) != Ordering::Greater => {}
            _ => {
                map.insert(r.item_id.clone(), r.clone());
            }
        }
    }
    map
}
