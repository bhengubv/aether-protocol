// SPDX-License-Identifier: MIT

//! Unit tests for the AetherNet Bandwidth Measurement Framework (ABMF).

#[cfg(test)]
mod tests {
    use std::sync::{Arc, Mutex};
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

    use crate::bandwidth::estimator::BandwidthEstimator;
    use crate::bandwidth::director::BandwidthDirector;
    use crate::bandwidth::models::{
        BandwidthConfidence, BandwidthGossipPayload, BandwidthProbeAck,
        NodeActivityState,
    };
    use crate::bandwidth::monitor::NodeActivityMonitor;

    // ── Helper ────────────────────────────────────────────────────────────────

    fn now_us() -> i64 {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_micros() as i64
    }

    // ── BandwidthEstimator tests ──────────────────────────────────────────────

    #[test]
    fn estimator_initial_confidence_is_none() {
        let est = BandwidthEstimator::new("BLE", 2_000_000);
        let s = est.current_sample();
        assert_eq!(s.confidence, BandwidthConfidence::None);
        assert_eq!(s.transport_name, "BLE");
    }

    #[test]
    fn estimator_record_delivery_increases_btl_bw() {
        let est = BandwidthEstimator::new("BLE", 1_000_000);
        let send_us = now_us();
        let deliver_us = send_us + 10_000; // 10 ms later
        // 10 000 bytes delivered in 10 ms = 8 Mbps
        est.record_delivery(10_000, send_us, deliver_us);
        let s = est.current_sample();
        assert!(s.btl_bw_bps > 0, "btl_bw should be positive after delivery");
        assert_eq!(s.confidence, BandwidthConfidence::Low); // 1 probe_round
    }

    #[test]
    fn estimator_confidence_advances_with_rounds() {
        let est = BandwidthEstimator::new("Wi-Fi Direct", 100_000_000);
        let base_us = now_us();
        for i in 0..25 {
            let send_us = base_us + i * 50_000;
            let deliver_us = send_us + 5_000;
            est.record_delivery(1_000, send_us, deliver_us);
        }
        let s = est.current_sample();
        assert_eq!(s.confidence, BandwidthConfidence::High);
    }

    #[test]
    fn estimator_record_loss_increases_loss_rate() {
        let est = BandwidthEstimator::new("BLE", 1_000_000);
        // Record some loss
        for _ in 0..10 {
            est.record_loss(512);
        }
        let s = est.current_sample();
        assert!(s.loss_rate > 0.0, "loss_rate should be positive after losses");
    }

    #[test]
    fn estimator_rto_clamped() {
        let est = BandwidthEstimator::new("BLE", 1_000_000);
        let send_us = now_us();
        let deliver_us = send_us + 50_000; // 50 ms
        est.record_delivery(1_000, send_us, deliver_us);
        let s = est.current_sample();
        let rto = s.rto();
        assert!(rto >= Duration::from_millis(200), "RTO must be ≥ 200 ms");
        assert!(rto <= Duration::from_secs(60), "RTO must be ≤ 60 s");
    }

    #[test]
    fn estimator_effective_bps_respects_phy_cap() {
        let est = BandwidthEstimator::new("BLE", 10_000_000);
        let send_us = now_us();
        let deliver_us = send_us + 1_000;
        est.record_delivery(100_000, send_us, deliver_us); // pushes btl_bw high
        est.apply_phy_hint(-90); // weak signal → cap at 125 kbps
        let s = est.current_sample();
        assert!(
            s.effective_bps() <= 125_000,
            "effective_bps should be capped by PHY hint"
        );
    }

    #[test]
    fn estimator_warm_from_gossip_seeds_estimate() {
        let est = BandwidthEstimator::new("NearLink", 50_000_000);
        assert_eq!(est.current_sample().confidence, BandwidthConfidence::None);
        est.warm_from_gossip(
            5_000_000,
            Duration::from_millis(20),
            BandwidthConfidence::Medium,
        );
        let s = est.current_sample();
        assert!(s.btl_bw_bps > 0, "btl_bw should be seeded by gossip");
        assert_eq!(s.confidence, BandwidthConfidence::Low); // warmed but no probe_rounds yet → Low
    }

    #[test]
    fn estimator_warm_from_gossip_does_not_downgrade() {
        let est = BandwidthEstimator::new("BLE", 2_000_000);
        let base_us = now_us();
        for i in 0..25 {
            let s = base_us + i * 20_000;
            est.record_delivery(1_000, s, s + 5_000);
        }
        let before = est.current_sample().btl_bw_bps;
        // Gossip with much lower bandwidth — should be ignored.
        est.warm_from_gossip(100, Duration::from_millis(1), BandwidthConfidence::Low);
        let after = est.current_sample().btl_bw_bps;
        assert_eq!(before, after, "gossip should not downgrade an established estimate");
    }

    #[test]
    fn estimator_probe_result_updates_estimate() {
        let est = BandwidthEstimator::new("BLE", 2_000_000);
        let now = now_us();
        let ack = BandwidthProbeAck {
            sequence: 1,
            sender_send_us: now,
            receiver_receive_us: now + 10_000,
            receiver_send_us: now + 11_000,
            sender_receive_us: now + 21_000,
            probe_bytes: 1_024,
        };
        let rtt = ack.rtt();
        assert!(rtt > Duration::ZERO);
        est.record_probe_result(&ack, now + 21_000);
        let s = est.current_sample();
        assert!(s.btl_bw_bps > 0);
    }

    #[test]
    fn probe_ack_rtt_and_owd() {
        let ack = BandwidthProbeAck {
            sequence: 42,
            sender_send_us: 1_000_000,
            receiver_receive_us: 1_010_000,
            receiver_send_us: 1_010_500,
            sender_receive_us: 1_020_000,
            probe_bytes: 512,
        };
        // RTT = (1_020_000 - 1_000_000) - (1_010_500 - 1_010_000) = 20_000 - 500 = 19_500 µs
        assert_eq!(ack.rtt(), Duration::from_micros(19_500));
        // Forward OWD = 1_010_000 - 1_000_000 = 10_000 µs
        assert_eq!(ack.forward_owd(), Duration::from_micros(10_000));
    }

    #[test]
    fn estimator_on_sample_improved_fires() {
        use std::sync::atomic::{AtomicU32, Ordering};
        let fired = Arc::new(AtomicU32::new(0));
        let est = BandwidthEstimator::new("BLE", 1_000_000);
        let fired_clone = Arc::clone(&fired);
        est.on_sample_improved(Box::new(move |_| {
            fired_clone.fetch_add(1, Ordering::Relaxed);
        }));
        let send_us = now_us();
        est.record_delivery(1_000, send_us, send_us + 10_000);
        assert!(fired.load(Ordering::Relaxed) > 0, "callback should fire");
    }

    // ── BandwidthDirector tests ───────────────────────────────────────────────

    #[test]
    fn director_recommend_falls_back_to_lowest_power_cost() {
        let dir = BandwidthDirector::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("NearLink", 10_000_000)));
        dir.register(Arc::clone(&est));
        let rec = dir.recommend_transport("peer-a", 1_000);
        assert_eq!(rec.as_deref(), Some("NearLink"));
    }

    #[test]
    fn director_get_estimate_returns_none_for_unknown_transport() {
        let dir = BandwidthDirector::new();
        assert!(dir.get_estimate("peer-x", "BLE").is_none());
    }

    #[test]
    fn director_apply_gossip_seeds_matrix() {
        let dir = BandwidthDirector::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        dir.register(Arc::clone(&est));

        let payload = BandwidthGossipPayload {
            peer_uhid: "peer-a".to_string(),
            transport_name: "BLE".to_string(),
            btl_bw_bps: 1_500_000,
            rt_prop_us: 20_000,
            confidence: BandwidthConfidence::Medium,
            measured_at: SystemTime::now(),
        };
        dir.apply_gossip(payload);
        let s = dir.get_estimate("peer-a", "BLE");
        assert!(s.is_some(), "matrix should be seeded after apply_gossip");
    }

    #[test]
    fn director_get_estimate_falls_back_to_local_estimator_before_gossip() {
        // Regression: a locally-probed transport must be a selection candidate BEFORE any gossip
        // arrives. The director previously consulted only the gossiped (peer × transport) matrix,
        // so get_estimate returned None for a registered-but-not-yet-gossiped transport — a node's
        // own measurements were invisible to transport selection until some peer happened to gossip
        // the same pair. get_estimate now falls back to the live registered estimator's sample.
        let dir = BandwidthDirector::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        dir.register(Arc::clone(&est));

        // Drive the estimator to a confident sample via local probes only — crucially NO
        // apply_gossip(), so the (peer × transport) matrix stays empty.
        let base_us = now_us();
        for i in 0..10 {
            let s = base_us + i * 20_000;
            est.lock().unwrap().record_delivery(1_000, s, s + 5_000);
        }
        assert_ne!(
            est.lock().unwrap().current_sample().confidence,
            BandwidthConfidence::None,
            "estimator should be confident after local probes",
        );

        // The fix: the confident local sample surfaces even though nothing was ever gossiped.
        let sample = dir.get_estimate("never-gossiped-peer", "BLE");
        assert!(sample.is_some(), "locally-probed transport must surface before gossip");
        assert_eq!(sample.unwrap().transport_name, "BLE");

        // Guard the boundaries: a registered-but-un-probed transport (confidence None) must NOT
        // surface, and a transport that was never registered remains None.
        let cold = Arc::new(Mutex::new(BandwidthEstimator::new("Wi-Fi Direct", 30_000_000)));
        dir.register(Arc::clone(&cold));
        assert!(
            dir.get_estimate("never-gossiped-peer", "Wi-Fi Direct").is_none(),
            "un-probed registered transport must not surface a None-confidence sample",
        );
        assert!(dir.get_estimate("never-gossiped-peer", "QUIC Relay").is_none());
    }

    #[test]
    fn director_build_gossip_payload_returns_none_when_no_confidence() {
        let dir = BandwidthDirector::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        dir.register(Arc::clone(&est));
        // No probes yet → confidence is None → should not emit gossip.
        assert!(dir.build_gossip_payload("peer-b", "BLE").is_none());
    }

    #[test]
    fn director_build_gossip_payload_returns_some_after_probes() {
        let dir = BandwidthDirector::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        dir.register(Arc::clone(&est));
        let base_us = now_us();
        for i in 0..10 {
            let s = base_us + i * 20_000;
            est.lock().unwrap().record_delivery(1_000, s, s + 5_000);
        }
        let payload = dir.build_gossip_payload("peer-c", "BLE");
        assert!(payload.is_some(), "gossip payload should be emitted after probes");
    }

    #[test]
    fn director_recommend_selects_highest_score() {
        let dir = BandwidthDirector::new();

        // BLE: warm with high bandwidth
        let ble = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        dir.register(Arc::clone(&ble));
        let ble_gossip = BandwidthGossipPayload {
            peer_uhid: "peer-z".to_string(),
            transport_name: "BLE".to_string(),
            btl_bw_bps: 500_000,
            rt_prop_us: 10_000,
            confidence: BandwidthConfidence::Medium,
            measured_at: SystemTime::now(),
        };
        dir.apply_gossip(ble_gossip);

        // NearLink: warm with much higher bandwidth
        let nl = Arc::new(Mutex::new(BandwidthEstimator::new("NearLink", 50_000_000)));
        dir.register(Arc::clone(&nl));
        let nl_gossip = BandwidthGossipPayload {
            peer_uhid: "peer-z".to_string(),
            transport_name: "NearLink".to_string(),
            btl_bw_bps: 20_000_000,
            rt_prop_us: 2_000,
            confidence: BandwidthConfidence::Medium,
            measured_at: SystemTime::now(),
        };
        dir.apply_gossip(nl_gossip);

        let rec = dir.recommend_transport("peer-z", 1_000);
        // NearLink has lower power cost (1 vs 2) AND higher bandwidth → should win.
        assert_eq!(rec.as_deref(), Some("NearLink"));
    }

    // ── NodeActivityMonitor tests ─────────────────────────────────────────────

    #[test]
    fn monitor_initial_state_is_offline() {
        let mon = NodeActivityMonitor::new();
        let s = mon.current();
        assert_eq!(s.state, NodeActivityState::Offline);
        assert_eq!(s.total_bps(), 0);
        assert!(!s.has_activity());
    }

    #[test]
    fn monitor_register_and_record_does_not_panic() {
        let mon = NodeActivityMonitor::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        mon.register("BLE", Arc::clone(&est));
        // Should not panic even if transport is registered.
        mon.record_ingress("BLE", 512);
        mon.record_egress("BLE", 256);
    }

    #[test]
    fn monitor_subscribe_callback_receives_snapshots() {
        use std::sync::atomic::{AtomicU32, Ordering};
        let count = Arc::new(AtomicU32::new(0));
        let mon = NodeActivityMonitor::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        // Set interval first, then register, then subscribe, then start.
        mon.set_sample_interval_ms(100);
        mon.register("BLE", est);
        let c = Arc::clone(&count);
        mon.subscribe(Box::new(move |_snap| {
            c.fetch_add(1, Ordering::Relaxed);
        }));
        mon.start();
        // Wait ~600 ms — with 100 ms interval we expect ~5 ticks.
        std::thread::sleep(Duration::from_millis(600));
        mon.stop();
        let fired = count.load(Ordering::Relaxed);
        assert!(fired >= 2, "subscriber should have fired at least twice, got {fired}");
    }

    #[test]
    fn monitor_active_peers_counts_distinct_peers() {
        let mon = NodeActivityMonitor::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        mon.set_sample_interval_ms(100);
        mon.register("BLE", est);
        // Two distinct peers send egress just before the tick.
        mon.record_egress_to_peer("BLE", "peer-a", 256);
        mon.record_egress_to_peer("BLE", "peer-b", 256);
        mon.start();
        // Wait long enough for at least one tick (interval is 100 ms).
        std::thread::sleep(Duration::from_millis(300));
        mon.stop();
        let s = mon.current();
        assert!(
            s.active_peers >= 2,
            "expected at least 2 active peers, got {}",
            s.active_peers
        );
    }

    #[test]
    fn monitor_active_peers_stays_zero_without_peer() {
        let mon = NodeActivityMonitor::new();
        let est = Arc::new(Mutex::new(BandwidthEstimator::new("BLE", 2_000_000)));
        mon.set_sample_interval_ms(100);
        mon.register("BLE", est);
        // Egress recorded WITHOUT a peer — must not contribute to active_peers.
        mon.record_egress("BLE", 256);
        mon.record_ingress("BLE", 256);
        mon.start();
        std::thread::sleep(Duration::from_millis(300));
        mon.stop();
        let s = mon.current();
        assert_eq!(
            s.active_peers, 0,
            "active_peers must stay 0 when no peer is supplied, got {}",
            s.active_peers
        );
    }

    // ── Model tests ───────────────────────────────────────────────────────────

    #[test]
    fn node_activity_snapshot_has_activity() {
        use crate::bandwidth::models::NodeActivitySnapshot;
        let make = |state: NodeActivityState| NodeActivitySnapshot {
            state,
            ingress_bps: 0,
            egress_bps: 0,
            active_peers: 0,
            active_transports: 0,
            transports: Vec::new(),
            primary_transport_name: None,
            timestamp: SystemTime::now(),
        };
        assert!(!make(NodeActivityState::Offline).has_activity());
        assert!(!make(NodeActivityState::Idle).has_activity());
        assert!(make(NodeActivityState::Active).has_activity());
        assert!(make(NodeActivityState::Busy).has_activity());
        assert!(make(NodeActivityState::Degraded).has_activity());
    }

    #[test]
    fn confidence_ordering() {
        assert!(BandwidthConfidence::None < BandwidthConfidence::Low);
        assert!(BandwidthConfidence::Low < BandwidthConfidence::Medium);
        assert!(BandwidthConfidence::Medium < BandwidthConfidence::High);
    }
}
