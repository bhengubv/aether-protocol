// SPDX-License-Identifier: MIT
//! Integration tests for PredictiveTransportSelector — Kalman RTT filter and scoring.

use async_trait::async_trait;
use std::sync::Arc;

use aethernet_protocol::transport::{
    PerTransportMetrics, PredictiveTransportSelector, TransportService,
};

// ── FakeTransport — minimal TransportService stub ─────────────────────────────

struct FakeTransport {
    name:          String,
    bandwidth_bps: i64,
    power_cost:    i32,
    available:     bool,
    metrics:       Arc<PerTransportMetrics>,
}

impl FakeTransport {
    fn arc(name: &str, bps: i64, power: i32, available: bool) -> Arc<dyn TransportService> {
        Arc::new(Self {
            name:          name.to_string(),
            bandwidth_bps: bps,
            power_cost:    power,
            available,
            metrics:       PerTransportMetrics::new(),
        })
    }
}

#[async_trait]
impl TransportService for FakeTransport {
    fn name(&self)                 -> &str  { &self.name }
    fn is_available(&self)         -> bool  { self.available }
    fn max_bandwidth_bps(&self)    -> i64   { self.bandwidth_bps }
    fn max_range_meters(&self)     -> i32   { 100 }
    fn power_cost_relative(&self)  -> i32   { self.power_cost }
    fn max_concurrent_peers(&self) -> i32   { 10 }
    fn metrics(&self) -> Option<Arc<PerTransportMetrics>> { Some(self.metrics.clone()) }
    fn is_connected(&self, _: &str) -> bool { false }
    fn set_data_received_handler(&mut self, _: Box<dyn Fn(&str, &[u8]) + Send + Sync>) {}

    async fn send_async(
        &self, _: &str, _: &[u8],
    ) -> Result<bool, Box<dyn std::error::Error>> {
        Ok(true)
    }
    async fn send_stream_async(
        &self, _: &str,
        _: &mut (dyn std::io::Read + Send + Unpin),
    ) -> Result<bool, Box<dyn std::error::Error>> {
        Ok(true)
    }
}

// ── Kalman filter (indirect) ──────────────────────────────────────────────────

#[test]
fn kalman_converges_on_steady_state() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    sel.register(t.clone(), 200.0);

    for _ in 0..50 { sel.observe_metrics(&t, 100, true, 1000); }

    let (rtt, _, _) = sel.kalman_state(&t).expect("kalman_state should return Some");
    assert!(
        (rtt - 100.0).abs() < 5.0,
        "Kalman did not converge: rtt={rtt:.2}, want ~100",
    );
}

#[test]
fn kalman_variance_decreases_with_observations() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    sel.register(t.clone(), 200.0);

    let (_, _, initial_var) = sel.kalman_state(&t).expect("initial state");
    for _ in 0..10 { sel.observe_metrics(&t, 200, true, 1000); }
    let (_, _, after_var) = sel.kalman_state(&t).expect("post-observation state");

    assert!(
        after_var < initial_var,
        "posterior variance {after_var:.4} should be < initial {initial_var:.4}",
    );
}

#[test]
fn kalman_detects_positive_drift() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    sel.register(t.clone(), 100.0);

    for i in 0..10u64 { sel.observe_metrics(&t, 100 + (i + 1) * 15, true, 1000); }

    let (_, drift, _) = sel.kalman_state(&t).expect("state");
    assert!(drift > 0.0, "drift {drift:.4} should be positive for rising RTT");
}

// ── PredictiveTransportSelector lifecycle ─────────────────────────────────────

#[test]
fn register_and_rank_fast_transport_first() {
    let sel  = PredictiveTransportSelector::new();
    let fast = FakeTransport::arc("fast", 1_000_000, 1,  true);
    let slow = FakeTransport::arc("slow",    10_000, 10, true);
    sel.register(fast.clone(), 50.0);
    sel.register(slow.clone(), 150.0);

    for _ in 0..5 { sel.observe_metrics(&fast, 50, true, 1000); }

    let ranked = sel.rank(100);
    assert_eq!(ranked.len(), 2);
    assert_eq!(
        ranked[0].transport.name(), "fast",
        "expected 'fast' first, got '{}'", ranked[0].transport.name(),
    );
}

#[test]
fn unavailable_transport_excluded_from_rank() {
    let sel     = PredictiveTransportSelector::new();
    let avail   = FakeTransport::arc("avail",   500_000, 1, true);
    let unavail = FakeTransport::arc("unavail", 500_000, 1, false);
    sel.register(avail.clone(),   100.0);
    sel.register(unavail.clone(), 100.0);

    let ranked = sel.rank(64);
    assert_eq!(ranked.len(), 1);
    assert_eq!(ranked[0].transport.name(), "avail");
}

#[test]
fn unregister_removes_transport() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    sel.register(t.clone(), 100.0);
    sel.unregister(&t);
    assert_eq!(sel.rank(64).len(), 0);
}

#[test]
fn select_best_returns_none_when_empty() {
    let sel = PredictiveTransportSelector::new();
    assert!(sel.select_best(64).is_none());
}

#[test]
fn duplicate_register_ignored() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    sel.register(t.clone(), 100.0);
    sel.register(t.clone(), 200.0);
    assert_eq!(sel.rank(64).len(), 1, "duplicate register should not double-add");
}

#[test]
fn kalman_state_initial_values() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    sel.register(t.clone(), 123.0);

    let (rtt, drift, variance) = sel.kalman_state(&t).expect("state");
    assert!((rtt - 123.0).abs() < 1e-9, "initial rtt {rtt} != 123.0");
    assert!(drift.abs() < 1e-9,          "initial drift {drift} != 0.0");
    assert!(variance > 0.0,              "initial variance {variance} should be > 0");
}

#[test]
fn kalman_state_unregistered_returns_none() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    assert!(sel.kalman_state(&t).is_none());
}

#[test]
fn rank_returns_positive_score() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    sel.register(t.clone(), 100.0);

    let ranked = sel.rank(64);
    assert_eq!(ranked.len(), 1);
    assert!(ranked[0].score > 0.0, "score should be positive");
}

#[test]
fn score_improves_after_good_observations() {
    let sel = PredictiveTransportSelector::new();
    let t   = FakeTransport::arc("t", 500_000, 1, true);
    sel.register(t.clone(), 200.0);
    let score_before = sel.rank(64)[0].score;

    for _ in 0..10 { sel.observe_metrics(&t, 20, true, 5000); }

    let score_after = sel.rank(64)[0].score;
    assert!(
        score_after > score_before,
        "score should improve after good observations (before={score_before:.4}, after={score_after:.4})",
    );
}
