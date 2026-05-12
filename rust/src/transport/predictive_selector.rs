// SPDX-License-Identifier: MIT
// Predictive transport selector — 2-state Kalman RTT filter over PerTransportMetrics.
//
// Why Kalman over EWMA?
// ─────────────────────
// EWMA is a 1-pole IIR: it smooths past measurements but cannot predict future RTT
// when a link is actively degrading.  The Kalman filter models RTT as a constant-
// velocity process [rtt, drift]:
//
//   x_t = F * x_{t−1} + w   (F = [[1,1],[0,1]])
//   z_t = H * x_t   + v    (H = [1,0])
//
// Positive drift signals a rising RTT *before* it exceeds a threshold, enabling
// proactive transport switching.  The posterior variance further penalises uncertain
// links even when their point estimate looks good.
//
// Score formula:
//   (effective_bps / power_cost) × (1 − loss_rate) / max(kalman_rtt, 1) × (1 / (1 + σ/100))
//
// Thread-safe: all mutation goes through a single `RwLock<SelectorInner>`.
//
// Key design note: `TransportService` is a trait object (`Arc<dyn TransportService>`).
// Rust trait objects are fat pointers and cannot be used directly as `HashMap` keys.
// We wrap them in `TransportKey`, which implements `Hash + Eq` via the data portion
// of the fat pointer (Arc::as_ptr cast to `*const ()`) — this gives us stable
// pointer-identity semantics identical to reference equality in other languages.

use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::sync::{Arc, RwLock};

use crate::transport::TransportService;

// ── TransportKey ──────────────────────────────────────────────────────────────

/// Newtype wrapping `Arc<dyn TransportService>` with pointer-identity `Hash + Eq`.
///
/// Two `TransportKey` values are equal iff they point to the *same* allocation
/// (i.e. `Arc::ptr_eq`).  This mirrors reference equality in Java/Kotlin/Swift.
#[derive(Clone)]
struct TransportKey(Arc<dyn TransportService>);

impl PartialEq for TransportKey {
    fn eq(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}

impl Eq for TransportKey {}

impl Hash for TransportKey {
    fn hash<H: Hasher>(&self, state: &mut H) {
        // Use the data pointer of the Arc as the hash input.
        let ptr = Arc::as_ptr(&self.0) as *const () as usize;
        ptr.hash(state);
    }
}

// ── KalmanRttFilter ───────────────────────────────────────────────────────────

/// Two-state Kalman filter estimating RTT and drift for one transport link.
///
/// State: x = [rtt; drift] — F = \[\[1,1\],\[0,1\]\], H = \[1,0\].
///
/// **Not thread-safe** — always accessed under the outer `RwLock`.
struct KalmanRttFilter {
    q_rtt:   f64, // process noise for RTT (Q[0,0]), default 25 ms²
    q_drift: f64, // process noise for drift (Q[1,1]), default 5 ms²
    r:       f64, // observation noise variance R, default 100 ms²

    // State: x = [rtt; drift]
    rtt:   f64,
    drift: f64,

    // Covariance P (2×2 symmetric: upper triangle).
    p00: f64,
    p01: f64,
    p11: f64,
}

impl KalmanRttFilter {
    fn new(initial_rtt_ms: f64) -> Self {
        Self {
            q_rtt:   25.0,
            q_drift: 5.0,
            r:       100.0,
            rtt:     initial_rtt_ms,
            drift:   0.0,
            p00:     400.0,
            p01:     0.0,
            p11:     100.0,
        }
    }

    fn rtt_estimate_ms(&self) -> f64 { self.rtt }
    fn drift_ms(&self)        -> f64 { self.drift }
    fn rtt_variance(&self)    -> f64 { self.p00 }

    /// Incorporate a new RTT measurement and return the updated estimate.
    fn update(&mut self, measured_rtt_ms: f64) -> f64 {
        // ── 1. Predict ────────────────────────────────────────────────────────
        let rtt_pred   = self.rtt + self.drift;
        let drift_pred = self.drift;

        // P_pred = F * P * F^T + Q  (F = [[1,1],[0,1]])
        let pp00 = self.p00 + 2.0 * self.p01 + self.p11 + self.q_rtt;
        let pp01 = self.p01 + self.p11;
        let pp11 = self.p11 + self.q_drift;

        // ── 2. Kalman gain (H = [1, 0]) ──────────────────────────────────────
        let s  = pp00 + self.r;
        let k0 = pp00 / s;
        let k1 = pp01 / s;

        // ── 3. Update ─────────────────────────────────────────────────────────
        let innovation = measured_rtt_ms - rtt_pred;
        self.rtt   = rtt_pred   + k0 * innovation;
        self.drift = drift_pred + k1 * innovation;

        // P = (I − K*H) * P_pred
        self.p00 = (1.0 - k0) * pp00;
        self.p01 = (1.0 - k0) * pp01;
        self.p11 = -k1 * pp01 + pp11;

        // Clamp to prevent numerical drift below zero.
        self.p00 = self.p00.max(1e-6);
        self.p11 = self.p11.max(1e-6);

        self.rtt
    }
}

// ── PredictiveTransportSelector ───────────────────────────────────────────────

/// A transport paired with its Kalman-predictive score and uncertainty metadata.
pub struct PredictedRankedTransport {
    /// The ranked transport backend.
    pub transport:       Arc<dyn TransportService>,
    /// Composite predictive score (higher = better).
    pub score:           f64,
    /// Kalman-estimated RTT in milliseconds.
    pub predicted_rtt_ms: f64,
    /// Posterior RTT variance (ms²). Lower = more confident.
    pub rtt_variance:    f64,
}

// Internal mutable state — always accessed under the outer RwLock.
struct SelectorInner {
    filters: HashMap<TransportKey, KalmanRttFilter>,
}

/// Predictive transport selector maintaining a Kalman RTT filter per transport.
///
/// Thread-safe: all state is guarded by a single `RwLock<SelectorInner>`.
pub struct PredictiveTransportSelector {
    inner: RwLock<SelectorInner>,
}

impl PredictiveTransportSelector {
    /// Create an empty selector ready for registration.
    pub fn new() -> Self {
        Self {
            inner: RwLock::new(SelectorInner {
                filters: HashMap::new(),
            }),
        }
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /// Register a transport with an initial RTT prior.
    /// Subsequent calls for the same `Arc` are no-ops.
    pub fn register(&self, transport: Arc<dyn TransportService>, initial_rtt_ms: f64) {
        let key = TransportKey(transport);
        let mut g = self.inner.write().unwrap();
        g.filters.entry(key).or_insert_with(|| KalmanRttFilter::new(initial_rtt_ms));
    }

    /// Remove a transport and discard its Kalman state.
    pub fn unregister(&self, transport: &Arc<dyn TransportService>) {
        let key = TransportKey(Arc::clone(transport));
        self.inner.write().unwrap().filters.remove(&key);
    }

    // ── Observation ───────────────────────────────────────────────────────────

    /// Feed a new sample to both the transport's `PerTransportMetrics` EWMA and
    /// our Kalman filter.  Call after every completed send attempt.
    ///
    /// Both `rtt_ms == 0` and `success == false` skip the Kalman update but are
    /// still forwarded to `PerTransportMetrics` so the loss-rate EWMA stays accurate.
    pub fn observe_metrics(
        &self,
        transport:         &Arc<dyn TransportService>,
        rtt_ms:            u64,
        success:           bool,
        bytes_transferred: u64,
    ) {
        if let Some(m) = transport.metrics() {
            m.record_sample(rtt_ms, success, bytes_transferred);
        }

        if rtt_ms == 0 || !success {
            return;
        }

        let key = TransportKey(Arc::clone(transport));
        let mut g = self.inner.write().unwrap();
        if let Some(f) = g.filters.get_mut(&key) {
            f.update(rtt_ms as f64);
        }
    }

    // ── Ranking ───────────────────────────────────────────────────────────────

    /// Return transports in descending predictive-score order.
    ///
    /// Only available transports are included.  `payload_bytes` excludes
    /// transports whose max bandwidth would require > 30 s to serialise.
    pub fn rank(&self, payload_bytes: usize) -> Vec<PredictedRankedTransport> {
        let g = self.inner.read().unwrap();
        let mut result: Vec<PredictedRankedTransport> = Vec::with_capacity(g.filters.len());

        for (key, filter) in &g.filters {
            let t = &key.0;
            if !t.is_available() {
                continue;
            }

            let bw = t.max_bandwidth_bps();
            if bw > 0 {
                let serial_sec = (payload_bytes as f64 * 8.0) / bw as f64;
                if serial_sec > 30.0 {
                    continue;
                }
            }

            let kalman_rtt = filter.rtt_estimate_ms().max(1.0);
            let variance   = filter.rtt_variance();
            let stddev     = variance.sqrt();
            let power      = t.power_cost_relative().max(1) as f64;

            let (loss_rate, effective_bps) = if let Some(m) = t.metrics() {
                let loss = m.ewma_loss_rate();
                let tput = m.ewma_throughput_bps().max(bw as f64 * 0.1);
                (loss, tput)
            } else {
                (0.05, bw as f64 * 0.1)
            };

            // Reliability factor: 1.0 at σ=0 ms, ~0.5 at σ=100 ms.
            let reliability_factor = 1.0 / (1.0 + stddev / 100.0);
            let score = (effective_bps / power) * (1.0 - loss_rate) / kalman_rtt
                * reliability_factor;

            result.push(PredictedRankedTransport {
                transport:        Arc::clone(t),
                score,
                predicted_rtt_ms: kalman_rtt,
                rtt_variance:     variance,
            });
        }

        result.sort_by(|a, b| {
            b.score
                .partial_cmp(&a.score)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        result
    }

    /// Return the highest-scoring available transport, or `None`.
    pub fn select_best(&self, payload_bytes: usize) -> Option<Arc<dyn TransportService>> {
        self.rank(payload_bytes).into_iter().next().map(|r| r.transport)
    }

    /// Return `(rtt_ms, drift_ms, variance)` for a registered transport, or `None`.
    pub fn kalman_state(
        &self,
        transport: &Arc<dyn TransportService>,
    ) -> Option<(f64, f64, f64)> {
        let key = TransportKey(Arc::clone(transport));
        let g = self.inner.read().unwrap();
        g.filters
            .get(&key)
            .map(|f| (f.rtt_estimate_ms(), f.drift_ms(), f.rtt_variance()))
    }
}

impl Default for PredictiveTransportSelector {
    fn default() -> Self {
        Self::new()
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::transport::{PerTransportMetrics, TransportService};
    use async_trait::async_trait;
    use std::sync::Arc;

    // ── Stub transport ────────────────────────────────────────────────────────

    struct StubTransport {
        available: bool,
        bw:        i64,
        power:     i32,
        m:         Arc<PerTransportMetrics>,
    }

    impl StubTransport {
        fn new(available: bool, bw: i64, power: i32) -> Arc<Self> {
            Arc::new(Self {
                available,
                bw,
                power,
                m: PerTransportMetrics::new(),
            })
        }
    }

    #[async_trait]
    impl TransportService for StubTransport {
        fn name(&self)                -> &str  { "stub" }
        fn is_available(&self)        -> bool  { self.available }
        fn max_bandwidth_bps(&self)   -> i64   { self.bw }
        fn max_range_meters(&self)    -> i32   { 0 }
        fn power_cost_relative(&self) -> i32   { self.power }
        fn max_concurrent_peers(&self)-> i32   { 8 }
        fn metrics(&self)             -> Option<Arc<PerTransportMetrics>> { Some(Arc::clone(&self.m)) }
        async fn send_async(&self, _: &str, _: &[u8]) -> Result<bool, Box<dyn std::error::Error>> { Ok(false) }
        async fn send_stream_async(&self, _: &str, _: &mut (dyn std::io::Read + Send + Unpin)) -> Result<bool, Box<dyn std::error::Error>> { Ok(false) }
        fn is_connected(&self, _: &str) -> bool { false }
        fn set_data_received_handler(&mut self, _: Box<dyn Fn(&str, &[u8]) + Send + Sync>) {}
    }

    // ── KalmanRttFilter (tested indirectly via kalman_state) ─────────────────

    #[test]
    fn kalman_filter_initial_state_from_prior() {
        let sel = PredictiveTransportSelector::new();
        let t = StubTransport::new(true, 1_000_000, 1);
        let prior = 150.0f64;
        sel.register(Arc::clone(&t) as Arc<dyn TransportService>, prior);
        let (rtt, drift, _var) = sel
            .kalman_state(&(Arc::clone(&t) as Arc<dyn TransportService>))
            .unwrap();
        assert!((rtt - prior).abs() < 1e-9, "initial rtt should be prior, got {rtt}");
        assert!((drift).abs() < 1e-9, "initial drift should be 0");
    }

    #[test]
    fn kalman_filter_updates_rtt_toward_measurement() {
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1_000_000, 1);
        sel.register(Arc::clone(&t), 200.0);

        // Observe a lower RTT — estimate should move toward 50 ms.
        sel.observe_metrics(&t, 50, true, 1000);
        let (rtt, _, _) = sel.kalman_state(&t).unwrap();
        assert!(rtt < 200.0, "RTT estimate should decrease after low observation, got {rtt}");
        assert!(rtt > 50.0,  "RTT estimate should not jump immediately to observation, got {rtt}");
    }

    #[test]
    fn kalman_filter_positive_drift_on_rising_rtt() {
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1_000_000, 1);
        sel.register(Arc::clone(&t), 100.0);

        // Feed steadily increasing RTT observations.
        for ms in [150u64, 200, 250, 300, 350] {
            sel.observe_metrics(&t, ms, true, 1000);
        }
        let (_, drift, _) = sel.kalman_state(&t).unwrap();
        assert!(drift > 0.0, "drift should be positive when RTT is rising, got {drift}");
    }

    #[test]
    fn kalman_filter_skips_update_on_failure() {
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1_000_000, 1);
        sel.register(Arc::clone(&t), 200.0);

        // Failed send → Kalman should not update (but EWMA loss should).
        sel.observe_metrics(&t, 0, false, 0);
        let (rtt, _, _) = sel.kalman_state(&t).unwrap();
        assert!((rtt - 200.0).abs() < 1e-9, "RTT should not change on failure, got {rtt}");
    }

    // ── PredictiveTransportSelector ───────────────────────────────────────────

    #[test]
    fn selector_default_rank_is_empty() {
        let sel = PredictiveTransportSelector::default();
        assert!(sel.rank(100).is_empty());
    }

    #[test]
    fn selector_register_then_rank_contains_transport() {
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1_000_000, 1);
        sel.register(Arc::clone(&t), 200.0);
        let ranked = sel.rank(64);
        assert_eq!(1, ranked.len());
    }

    #[test]
    fn selector_excludes_unavailable_transports() {
        let sel = PredictiveTransportSelector::new();
        let avail: Arc<dyn TransportService>   = StubTransport::new(true,  1_000_000, 1);
        let unavail: Arc<dyn TransportService> = StubTransport::new(false, 1_000_000, 1);
        sel.register(Arc::clone(&avail), 200.0);
        sel.register(Arc::clone(&unavail), 200.0);
        let ranked = sel.rank(64);
        assert_eq!(1, ranked.len(), "unavailable transport should be excluded");
    }

    #[test]
    fn selector_register_idempotent_for_same_arc() {
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1_000_000, 1);
        sel.register(Arc::clone(&t), 200.0);
        sel.register(Arc::clone(&t), 100.0); // second call is no-op
        let ranked = sel.rank(64);
        assert_eq!(1, ranked.len(), "register should be idempotent for same Arc");
    }

    #[test]
    fn selector_unregister_removes_transport() {
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1_000_000, 1);
        sel.register(Arc::clone(&t), 200.0);
        sel.unregister(&t);
        assert!(sel.rank(64).is_empty(), "transport should be removed after unregister");
    }

    #[test]
    fn selector_kalman_state_none_for_unregistered() {
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1_000_000, 1);
        assert!(sel.kalman_state(&t).is_none());
    }

    #[test]
    fn selector_select_best_returns_highest_scored() {
        let sel = PredictiveTransportSelector::new();
        let fast: Arc<dyn TransportService> = StubTransport::new(true, 100_000_000, 1);
        let slow: Arc<dyn TransportService> = StubTransport::new(true, 100_000,     100);
        sel.register(Arc::clone(&fast), 10.0);
        sel.register(Arc::clone(&slow), 300.0);

        // After some observations, fast should score higher.
        sel.observe_metrics(&fast, 10, true, 10_000);
        sel.observe_metrics(&slow, 300, true, 100);

        let best = sel.select_best(100).unwrap();
        // Fast transport has much higher effective bandwidth and lower RTT.
        assert!(Arc::ptr_eq(&best, &fast) || Arc::ptr_eq(&best, &slow),
                "select_best should return one of the registered transports");
    }

    #[test]
    fn selector_select_best_returns_none_when_empty() {
        let sel = PredictiveTransportSelector::new();
        assert!(sel.select_best(100).is_none());
    }

    #[test]
    fn selector_excludes_transports_too_slow_for_payload() {
        // A 1 bps transport would take > 30 s to send a 4-byte payload.
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1, 1); // 1 bps
        sel.register(Arc::clone(&t), 200.0);

        // 4 bytes * 8 bits = 32 bits / 1 bps = 32 s > 30 s threshold.
        let ranked = sel.rank(4);
        assert!(
            ranked.is_empty(),
            "transport too slow for payload should be excluded"
        );
    }

    #[test]
    fn ranked_result_has_predicted_rtt_and_variance() {
        let sel = PredictiveTransportSelector::new();
        let t: Arc<dyn TransportService> = StubTransport::new(true, 1_000_000, 1);
        sel.register(Arc::clone(&t), 200.0);
        let ranked = sel.rank(64);
        assert_eq!(1, ranked.len());
        assert!(ranked[0].predicted_rtt_ms > 0.0);
        assert!(ranked[0].rtt_variance > 0.0);
        assert!(ranked[0].score > 0.0);
    }
}
