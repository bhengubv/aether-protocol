// SPDX-License-Identifier: MIT

//! Behavioural anomaly detection layered on top of [`NodeReputationService`].
//!
//! Three detection strategies are implemented:
//!
//! * **Volume spike** — per-source EWMA baseline with rolling windows; fires
//!   `record_rreq_flood_attempt` when a window's count exceeds
//!   `volume_spike_multiplier × old_ewma`.
//!
//! * **Destination scatter** — tracks unique destinations per source in a
//!   sliding time window; fires `record_rreq_flood_attempt` when the unique
//!   count exceeds `scatter_threshold`.
//!
//! * **Geohash claim** — compares the first `geohash_prefix_length` characters
//!   of a node's claimed geohash against its observed routing geohash; fires
//!   `record_signature_failure` on mismatch, rate-limited per UHID.

use std::collections::HashMap;
use std::sync::Arc;

use crate::reputation::NodeReputationService;

// ── Options ───────────────────────────────────────────────────────────────────

/// Tunable parameters for [`BehavioralAnomalyDetector`].
#[derive(Debug, Clone)]
pub struct AnomalyDetectorOptions {
    /// Duration of each volume-counting window in milliseconds.
    pub volume_window_ms: i64,
    /// A window's packet count must exceed `volume_spike_multiplier × old_ewma`
    /// before the detector fires.
    pub volume_spike_multiplier: f64,
    /// Smoothing factor for the EWMA baseline update (α in [0, 1]).
    pub ewma_alpha: f64,
    /// Sliding window duration for destination scatter detection.
    pub scatter_window_ms: i64,
    /// Maximum unique destination UHIDs allowed per source within the scatter window.
    pub scatter_threshold: usize,
    /// Number of leading characters compared when checking geohash claims.
    pub geohash_prefix_length: usize,
    /// Minimum milliseconds between geohash-mismatch signals for the same UHID.
    pub geohash_rate_limit_ms: i64,
}

impl Default for AnomalyDetectorOptions {
    fn default() -> Self {
        Self {
            volume_window_ms: 30_000,
            volume_spike_multiplier: 5.0,
            ewma_alpha: 0.20,
            scatter_window_ms: 60_000,
            scatter_threshold: 50,
            geohash_prefix_length: 4,
            geohash_rate_limit_ms: 60_000,
        }
    }
}

// ── Per-source volume state ───────────────────────────────────────────────────

#[derive(Debug, Default)]
struct VolumeState {
    window_start: i64,
    window_count: u64,
    ewma_baseline: f64,
    has_baseline: bool,
}

// ── Detector ──────────────────────────────────────────────────────────────────

/// Detects behavioural anomalies and feeds signals into a [`NodeReputationService`].
pub struct BehavioralAnomalyDetector {
    reputation: Arc<NodeReputationService>,
    opts: AnomalyDetectorOptions,

    /// Per-source volume rolling-window state.
    volume_states: HashMap<String, VolumeState>,

    /// Per-source timestamped destination entries `(dest_uhid, timestamp_ms)`.
    scatter_entries: HashMap<String, Vec<(String, i64)>>,

    /// Per-UHID timestamp of the last geohash-mismatch signal.
    geohash_last_signal: HashMap<String, i64>,
}

impl BehavioralAnomalyDetector {
    /// Creates a new detector backed by `reputation` with the given `opts`.
    pub fn new(reputation: Arc<NodeReputationService>, opts: AnomalyDetectorOptions) -> Self {
        Self {
            reputation,
            opts,
            volume_states: HashMap::new(),
            scatter_entries: HashMap::new(),
            geohash_last_signal: HashMap::new(),
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// Records a packet observation; runs volume-spike and scatter detection.
    pub fn observe_packet(
        &mut self,
        source_uhid: &str,
        dest_uhid: &str,
        timestamp_ms: i64,
    ) {
        self.check_volume_spike(source_uhid, timestamp_ms);
        self.check_scatter(source_uhid, dest_uhid, timestamp_ms);
    }

    /// Checks whether a node's claimed geohash matches its observed routing
    /// geohash; fires a signature-failure signal on mismatch (rate-limited).
    pub fn observe_geohash_claim(
        &mut self,
        uhid: &str,
        claimed_geohash: &str,
        observed_routing_geohash: &str,
        timestamp_ms: i64,
    ) {
        let prefix_len = self.opts.geohash_prefix_length;

        let claimed_prefix: String = claimed_geohash.chars().take(prefix_len).collect();
        let observed_prefix: String = observed_routing_geohash.chars().take(prefix_len).collect();

        if claimed_prefix == observed_prefix {
            return;
        }

        // Rate-limit: fire at most once per `geohash_rate_limit_ms` per UHID.
        let should_fire = match self.geohash_last_signal.get(uhid) {
            None => true,
            Some(&last) => timestamp_ms - last >= self.opts.geohash_rate_limit_ms,
        };

        if should_fire {
            self.reputation.record_signature_failure(uhid);
            self.geohash_last_signal.insert(uhid.to_string(), timestamp_ms);
        }
    }

    /// Directly passes through a SPK signature failure signal.
    pub fn observe_spk_sig_failure(&self, uhid: &str) {
        self.reputation.record_signature_failure(uhid);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    fn check_volume_spike(&mut self, source_uhid: &str, timestamp_ms: i64) {
        let state = self
            .volume_states
            .entry(source_uhid.to_string())
            .or_insert_with(VolumeState::default);

        // First ever observation for this source: open the first window.
        if state.window_count == 0 && !state.has_baseline {
            state.window_start = timestamp_ms;
            state.window_count = 1;
            return;
        }

        let window_expired =
            timestamp_ms - state.window_start >= self.opts.volume_window_ms;

        if !window_expired {
            // Still inside the current window — just count.
            state.window_count += 1;
        } else {
            // Window has expired: update EWMA and optionally fire.
            let completed_count = state.window_count;

            if !state.has_baseline {
                // First completed window: seed the baseline.
                state.ewma_baseline = completed_count as f64;
                state.has_baseline = true;
            } else {
                let old_ewma = state.ewma_baseline;
                let alpha = self.opts.ewma_alpha;
                state.ewma_baseline =
                    alpha * completed_count as f64 + (1.0 - alpha) * old_ewma;

                if completed_count as f64 > self.opts.volume_spike_multiplier * old_ewma
                    && old_ewma > 0.0
                {
                    self.reputation.record_rreq_flood_attempt(source_uhid);
                }
            }

            // Roll: start a new window at this timestamp with count = 1.
            state.window_start = timestamp_ms;
            state.window_count = 1;
        }
    }

    fn check_scatter(
        &mut self,
        source_uhid: &str,
        dest_uhid: &str,
        timestamp_ms: i64,
    ) {
        let window_ms = self.opts.scatter_window_ms;
        let threshold = self.opts.scatter_threshold;

        let entries = self
            .scatter_entries
            .entry(source_uhid.to_string())
            .or_default();

        // Prune entries outside the sliding window.
        entries.retain(|(_, ts)| timestamp_ms - ts < window_ms);

        // Add the new observation.
        entries.push((dest_uhid.to_string(), timestamp_ms));

        // Count unique destinations after deduplication.
        let mut seen: std::collections::HashSet<&str> = std::collections::HashSet::new();
        let unique_count = entries
            .iter()
            .filter(|(dest, _)| seen.insert(dest.as_str()))
            .count();

        if unique_count > threshold {
            self.reputation.record_rreq_flood_attempt(source_uhid);
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    const EPS: f64 = 1e-9;

    fn assert_near(label: &str, got: f64, expected: f64) {
        assert!(
            (got - expected).abs() < EPS,
            "{label}: expected {expected:.6}, got {got:.6}"
        );
    }

    fn make_detector(opts: AnomalyDetectorOptions) -> (BehavioralAnomalyDetector, Arc<NodeReputationService>) {
        let rep = Arc::new(NodeReputationService::new());
        let det = BehavioralAnomalyDetector::new(Arc::clone(&rep), opts);
        (det, rep)
    }

    fn test_opts() -> AnomalyDetectorOptions {
        AnomalyDetectorOptions {
            volume_window_ms: 100,
            volume_spike_multiplier: 2.0,
            ewma_alpha: 0.20,
            scatter_window_ms: 60_000,
            scatter_threshold: 3,
            geohash_prefix_length: 4,
            geohash_rate_limit_ms: 0, // no rate limit by default in tests
        }
    }

    // ── Test 1: first window only seeds, no penalty ───────────────────────────

    #[test]
    fn test_volume_no_spike_first_window() {
        let (mut det, rep) = make_detector(test_opts());
        // Send 5 packets inside the first window (t=0..4). Window never rolls.
        for t in 0..5_i64 {
            det.observe_packet("src-a", "dst-x", t);
        }
        // First window only seeds — no signal should have fired.
        assert_near("no spike first window", rep.get_reputation_score("src-a"), 1.0);
    }

    // ── Test 2: spike fires ───────────────────────────────────────────────────

    #[test]
    fn test_volume_spike_fires() {
        let (mut det, rep) = make_detector(test_opts());
        // Window 1: 5 packets (t=0..4) → seeds ewma_baseline = 5.0
        for t in 0..5_i64 {
            det.observe_packet("src-b", "dst-x", t);
        }
        // Window 2 trigger + body: t=200 rolls window 1 (seeds baseline=5), then 5 more
        det.observe_packet("src-b", "dst-x", 200); // rolls w1 → baseline seeded
        for t in 201..205_i64 {
            det.observe_packet("src-b", "dst-x", t);
        }
        // Window 3 trigger + body: t=400 rolls window 2 (ewma update; 5 ≈ 5, no spike), then 20
        det.observe_packet("src-b", "dst-x", 400); // rolls w2 → no spike (5 ≈ old_ewma 5)
        for t in 401..420_i64 {
            det.observe_packet("src-b", "dst-x", t);
        }
        // t=600 rolls window 3: count=20, old_ewma≈5 → 20 > 2.0*5 → spike fires
        det.observe_packet("src-b", "dst-x", 600);
        // rreq_flood_attempt fires → score < 1.0
        assert!(
            rep.get_reputation_score("src-b") < 1.0,
            "expected spike penalty, score was {}",
            rep.get_reputation_score("src-b")
        );
    }

    // ── Test 3: 1000 packets in one window, never rolls, score = 1.0 ─────────

    #[test]
    fn test_volume_no_spike_same_window() {
        let (mut det, rep) = make_detector(test_opts());
        // All within [0, 99) ms — window of 100 ms never expires.
        for i in 0..1000_i64 {
            det.observe_packet("src-c", "dst-x", i % 99);
        }
        assert_near("same window no roll", rep.get_reputation_score("src-c"), 1.0);
    }

    // ── Test 4: unique dests ≤ scatter_threshold, score = 1.0 ────────────────

    #[test]
    fn test_scatter_below_threshold() {
        let (mut det, rep) = make_detector(test_opts());
        // scatter_threshold = 3; send to exactly 3 unique destinations.
        for dest in ["dst-1", "dst-2", "dst-3"] {
            det.observe_packet("src-d", dest, 0);
        }
        assert_near("scatter below threshold", rep.get_reputation_score("src-d"), 1.0);
    }

    // ── Test 5: unique dests > scatter_threshold, score < 1.0 ────────────────

    #[test]
    fn test_scatter_above_threshold() {
        let (mut det, rep) = make_detector(test_opts());
        // scatter_threshold = 3; send to 4 unique destinations → fires
        for dest in ["dst-1", "dst-2", "dst-3", "dst-4"] {
            det.observe_packet("src-e", dest, 0);
        }
        assert!(
            rep.get_reputation_score("src-e") < 1.0,
            "expected scatter penalty, score was {}",
            rep.get_reputation_score("src-e")
        );
    }

    // ── Test 6: old entries expire, unique count resets, no false fire ────────

    #[test]
    fn test_scatter_prunes_old_entries() {
        let (mut det, rep) = make_detector(test_opts());
        // scatter_window_ms = 60_000. Send 4 unique dests at t=0 → fires once.
        for dest in ["dst-1", "dst-2", "dst-3", "dst-4"] {
            det.observe_packet("src-f", dest, 0);
        }
        // Score is now < 1.0 after the first fire; record it.
        let score_after_first_fire = rep.get_reputation_score("src-f");
        assert!(score_after_first_fire < 1.0);

        // Now send 3 new unique dests far in the future (t = 120_000 ms).
        // All previous entries should have been pruned (they are 120_000 ms old,
        // which is >= scatter_window_ms of 60_000). Only 3 new unique entries
        // remain → threshold not exceeded → no additional penalty.
        for dest in ["new-1", "new-2", "new-3"] {
            det.observe_packet("src-f", dest, 120_000);
        }
        assert_near(
            "no extra penalty after prune",
            rep.get_reputation_score("src-f"),
            score_after_first_fire,
        );
    }

    // ── Test 7: matching geohash prefix, score = 1.0 ─────────────────────────

    #[test]
    fn test_geohash_match_no_signal() {
        let (mut det, rep) = make_detector(test_opts());
        det.observe_geohash_claim("src-g", "ezs42xyz", "ezs42abc", 0);
        assert_near("geohash match", rep.get_reputation_score("src-g"), 1.0);
    }

    // ── Test 8: prefix mismatch fires, score = 0.80 ───────────────────────────

    #[test]
    fn test_geohash_mismatch_fires() {
        let (mut det, rep) = make_detector(test_opts());
        det.observe_geohash_claim("src-h", "ezs42xyz", "u4pruXXX", 0);
        // record_signature_failure → −0.20 → 0.80
        assert_near("geohash mismatch", rep.get_reputation_score("src-h"), 0.80);
    }

    // ── Test 9: second mismatch within rate limit, only one signal ────────────

    #[test]
    fn test_geohash_rate_limit_suppresses() {
        let mut opts = test_opts();
        opts.geohash_rate_limit_ms = 5_000; // 5-second rate limit
        let (mut det, rep) = make_detector(opts);

        // First mismatch at t=0 → fires
        det.observe_geohash_claim("src-i", "ezs42xyz", "u4pruXXX", 0);
        // Second mismatch at t=1000 (within 5000 ms) → suppressed
        det.observe_geohash_claim("src-i", "ezs42xyz", "u4pruXXX", 1_000);

        // Only one signal fired → 1.0 − 0.20 = 0.80
        assert_near("rate limited to one signal", rep.get_reputation_score("src-i"), 0.80);
    }

    // ── Test 10: SPK sig failure passthrough, score = 0.80 ───────────────────

    #[test]
    fn test_spk_sig_failure_passthrough() {
        let (det, rep) = make_detector(test_opts());
        det.observe_spk_sig_failure("src-j");
        // record_signature_failure → −0.20 → 0.80
        assert_near("spk sig failure passthrough", rep.get_reputation_score("src-j"), 0.80);
    }
}
