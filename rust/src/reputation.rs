// SPDX-License-Identifier: MIT

//! Per-UHID behavioural reputation scoring.
//!
//! Scores live in the range [0.0, 1.0].  Unknown peers default to 1.0 (benefit
//! of the doubt).  All mutations are applied via [`apply_delta`] which calls
//! [`clamp_score`] to enforce the boundary and epsilon-snap near 0 / 1.

use std::collections::HashMap;
use std::sync::{Arc, RwLock};

/// Aggregates per-UHID behavioural signals into a reputation score in [0.0, 1.0].
#[derive(Clone, Default)]
pub struct NodeReputationService {
    scores: Arc<RwLock<HashMap<String, f64>>>,
}

impl NodeReputationService {
    /// Creates a new service with no recorded scores.
    pub fn new() -> Self {
        Self {
            scores: Arc::new(RwLock::new(HashMap::new())),
        }
    }

    /// Records an RREQ flood attempt for `uhid` (delta: -0.05).
    pub fn record_rreq_flood_attempt(&self, uhid: &str) {
        let mut scores = self.scores.write().unwrap();
        apply_delta(&mut scores, uhid, -0.05);
    }

    /// Records a replay attempt for `uhid` (delta: -0.15).
    pub fn record_replay_attempt(&self, uhid: &str) {
        let mut scores = self.scores.write().unwrap();
        apply_delta(&mut scores, uhid, -0.15);
    }

    /// Records a signature verification failure for `uhid` (delta: -0.20).
    pub fn record_signature_failure(&self, uhid: &str) {
        let mut scores = self.scores.write().unwrap();
        apply_delta(&mut scores, uhid, -0.20);
    }

    /// Records a custody refusal for `uhid` (delta: -0.05).
    pub fn record_custody_refusal(&self, uhid: &str) {
        let mut scores = self.scores.write().unwrap();
        apply_delta(&mut scores, uhid, -0.05);
    }

    /// Records a successful delivery for `uhid` (delta: +0.01).
    ///
    /// `round_trip_ms` is accepted for future use (e.g. latency-weighted scoring)
    /// but is not currently incorporated into the score delta.
    pub fn record_delivery_success(&self, uhid: &str, _round_trip_ms: u32) {
        let mut scores = self.scores.write().unwrap();
        apply_delta(&mut scores, uhid, 0.01);
    }

    /// Records a delivery failure for `uhid` (delta: -0.02).
    pub fn record_delivery_failure(&self, uhid: &str) {
        let mut scores = self.scores.write().unwrap();
        apply_delta(&mut scores, uhid, -0.02);
    }

    /// Applies a pre-weighted delta (already scaled by reporter trust) to `uhid`.
    ///
    /// `weighted_delta` is clamped to [-1.0, 1.0] before being applied so that
    /// callers cannot accidentally pass an out-of-range value.
    pub fn apply_weighted_delta(&self, uhid: &str, weighted_delta: f64) {
        let clamped = weighted_delta.clamp(-1.0, 1.0);
        let mut scores = self.scores.write().unwrap();
        apply_delta(&mut scores, uhid, clamped);
    }

    /// Returns the current reputation score for `uhid`.
    ///
    /// Unknown peers return `1.0` (benefit of the doubt).
    pub fn get_reputation_score(&self, uhid: &str) -> f64 {
        let scores = self.scores.read().unwrap();
        *scores.get(uhid).unwrap_or(&1.0)
    }

    /// Returns a point-in-time snapshot of all recorded scores.
    pub fn get_all_scores(&self) -> HashMap<String, f64> {
        self.scores.read().unwrap().clone()
    }
}

// ── Internal helpers ──────────────────────────────────────────────────────────

/// Clamps `v` to [0.0, 1.0] with epsilon-snap:
/// - result < 1e-12  → 0.0
/// - result > 1.0 - 1e-12 → 1.0
fn clamp_score(v: f64) -> f64 {
    if v < 1e-12 {
        return 0.0;
    }
    if v > 1.0 - 1e-12 {
        return 1.0;
    }
    v
}

/// Applies `delta` to the score for `uhid`, inserting 1.0 as the base when
/// the peer is not yet known, and clamping the result with [`clamp_score`].
fn apply_delta(scores: &mut HashMap<String, f64>, uhid: &str, delta: f64) {
    let current = *scores.get(uhid).unwrap_or(&1.0);
    scores.insert(uhid.to_string(), clamp_score(current + delta));
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    const EPS: f64 = 1e-9;

    fn assert_near(label: &str, got: f64, expected: f64) {
        assert!(
            (got - expected).abs() < EPS,
            "{label}: expected {expected}, got {got}"
        );
    }

    #[test]
    fn unknown_peer_returns_one() {
        let svc = NodeReputationService::new();
        assert_near("unknown peer", svc.get_reputation_score("nobody"), 1.0);
    }

    #[test]
    fn rreq_flood_decrements_by_005() {
        let svc = NodeReputationService::new();
        svc.record_rreq_flood_attempt("peer-a");
        assert_near("rreq flood", svc.get_reputation_score("peer-a"), 0.95);
    }

    #[test]
    fn replay_attempt_decrements_by_015() {
        let svc = NodeReputationService::new();
        svc.record_replay_attempt("peer-b");
        assert_near("replay", svc.get_reputation_score("peer-b"), 0.85);
    }

    #[test]
    fn signature_failure_decrements_by_020() {
        let svc = NodeReputationService::new();
        svc.record_signature_failure("peer-c");
        assert_near("sig failure", svc.get_reputation_score("peer-c"), 0.80);
    }

    #[test]
    fn custody_refusal_decrements_by_005() {
        let svc = NodeReputationService::new();
        svc.record_custody_refusal("peer-d");
        assert_near("custody refusal", svc.get_reputation_score("peer-d"), 0.95);
    }

    #[test]
    fn delivery_failure_decrements_by_002() {
        let svc = NodeReputationService::new();
        svc.record_delivery_failure("peer-e");
        assert_near("delivery failure", svc.get_reputation_score("peer-e"), 0.98);
    }

    #[test]
    fn five_sig_failures_clamp_to_zero() {
        let svc = NodeReputationService::new();
        for _ in 0..5 {
            svc.record_signature_failure("peer-f");
        }
        // 1.0 - 5*0.20 = 0.0 — should be epsilon-snapped to exactly 0.0
        assert_near("5× sig failure", svc.get_reputation_score("peer-f"), 0.0);
    }

    #[test]
    fn ten_delivery_successes_clamp_to_one() {
        let svc = NodeReputationService::new();
        // Start from a depressed baseline so we can drive back to 1.0
        svc.record_signature_failure("peer-g"); // → 0.80
        for _ in 0..20 {
            svc.record_delivery_success("peer-g", 50);
        }
        // 0.80 + 20*0.01 = 1.0 — epsilon-snapped to exactly 1.0
        assert_near("20× delivery success from 0.80", svc.get_reputation_score("peer-g"), 1.0);
    }

    #[test]
    fn no_cross_contamination_between_peers() {
        let svc = NodeReputationService::new();
        svc.record_signature_failure("peer-h");
        // peer-i is completely independent
        assert_near("unaffected peer", svc.get_reputation_score("peer-i"), 1.0);
        assert_near("penalised peer", svc.get_reputation_score("peer-h"), 0.80);
    }

    #[test]
    fn get_all_scores_snapshot() {
        let svc = NodeReputationService::new();
        svc.record_rreq_flood_attempt("peer-j");
        svc.record_replay_attempt("peer-k");

        let snapshot = svc.get_all_scores();
        assert_eq!(snapshot.len(), 2);
        assert_near("snapshot peer-j", *snapshot.get("peer-j").unwrap(), 0.95);
        assert_near("snapshot peer-k", *snapshot.get("peer-k").unwrap(), 0.85);
    }

    #[test]
    fn compound_signals_reach_060() {
        let svc = NodeReputationService::new();
        // Starting at 1.0:
        //  -0.20 (sig)    → 0.80
        //  -0.15 (replay) → 0.65
        //  -0.05 (rreq)   → 0.60
        svc.record_signature_failure("peer-l");
        svc.record_replay_attempt("peer-l");
        svc.record_rreq_flood_attempt("peer-l");
        assert_near("compound", svc.get_reputation_score("peer-l"), 0.60);
    }
}
