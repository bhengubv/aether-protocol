// SPDX-License-Identifier: MIT

//! Cross-transport bandwidth synthesis and mesh gossip coordinator.
//!
//! Ports `src/AetherNet.Transport/Bandwidth/BandwidthDirector.cs`.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use super::estimator::BandwidthEstimator;
use super::models::{BandwidthConfidence, BandwidthGossipPayload, BandwidthSample};

// ── Power costs ───────────────────────────────────────────────────────────────

fn default_power_cost(transport: &str) -> f64 {
    match transport {
        "NearLink" => 1.0,
        "BLE" => 2.0,
        "Wi-Fi Direct" | "CircleLink" => 3.0,
        "QUIC Relay" | "HTTP Relay" => 10.0,
        _ => 5.0,
    }
}

// ── Director ──────────────────────────────────────────────────────────────────

/// Cross-transport bandwidth synthesis and mesh gossip coordinator.
///
/// Maintains a (peer_uhid × transport_name) → `BandwidthSample` matrix and
/// provides transport recommendations based on payload size, BDP, and power cost.
pub struct BandwidthDirector {
    // (peer_uhid, transport_name) → latest sample
    matrix: Mutex<HashMap<(String, String), BandwidthSample>>,
    // transport_name → estimator
    estimators: Mutex<HashMap<String, Arc<Mutex<BandwidthEstimator>>>>,
}

impl BandwidthDirector {
    pub fn new() -> Self {
        Self {
            matrix: Mutex::new(HashMap::new()),
            estimators: Mutex::new(HashMap::new()),
        }
    }

    // ── Registration ─────────────────────────────────────────────────────────

    /// Register an estimator. Call once per transport at startup.
    pub fn register(&self, estimator: Arc<Mutex<BandwidthEstimator>>) {
        let transport_name = estimator.lock().unwrap().transport_name.clone();
        // The estimator's live sample is consulted on every query/recommendation (see
        // get_estimate / get_estimates), so a locally-probed transport contributes to
        // selection even before any gossip arrives. This is a pull-based equivalent of
        // the C# reference's estimator-improvement -> matrix propagation (a push closure
        // that borrows `self` is awkward in Rust, so we pull on demand instead).
        self.estimators
            .lock()
            .unwrap()
            .insert(transport_name, estimator);
    }

    // ── Estimates ─────────────────────────────────────────────────────────────

    /// Get the bandwidth estimate for a specific peer on a specific transport.
    /// Returns `None` if no estimate exists yet.
    ///
    /// Reads the gossiped (peer x transport) matrix first; if there is no gossiped
    /// sample for this pair it falls back to the live local estimator for `transport`
    /// (when registered and already probed to a confident sample). This mirrors the C#
    /// reference, where estimator improvements propagate into the matrix.
    pub fn get_estimate(&self, peer_uhid: &str, transport: &str) -> Option<BandwidthSample> {
        {
            let m = self.matrix.lock().unwrap();
            if let Some(s) = m
                .iter()
                .find(|((peer, t), _)| {
                    peer.eq_ignore_ascii_case(peer_uhid) && t.eq_ignore_ascii_case(transport)
                })
                .map(|(_, s)| s.clone())
            {
                return Some(s);
            }
        }
        let g = self.estimators.lock().unwrap();
        g.iter()
            .find(|(name, _)| name.eq_ignore_ascii_case(transport))
            .map(|(_, est)| est.lock().unwrap().current_sample())
            .filter(|s| s.confidence != BandwidthConfidence::None)
    }

    /// Get all current estimates for a peer across all transports,
    /// ranked by `available_bps` descending.
    ///
    /// Merges the gossiped matrix for the peer with the live local-estimator samples for
    /// any probed transport not already covered by a gossiped entry, so a locally-measured
    /// transport is a selection candidate before first gossip (mirrors the C# reference's
    /// estimator -> matrix propagation). An un-probed transport (confidence None) is left
    /// out so a node with no measurements still falls through to the lowest-power default.
    pub fn get_estimates(&self, peer_uhid: &str) -> Vec<BandwidthSample> {
        let mut results: Vec<BandwidthSample> = Vec::new();
        let mut covered: std::collections::HashSet<String> = std::collections::HashSet::new();
        {
            let m = self.matrix.lock().unwrap();
            for ((peer, t), s) in m.iter() {
                if peer.eq_ignore_ascii_case(peer_uhid) {
                    results.push(s.clone());
                    covered.insert(t.to_ascii_lowercase());
                }
            }
        }
        {
            let g = self.estimators.lock().unwrap();
            for (name, est) in g.iter() {
                if !covered.contains(&name.to_ascii_lowercase()) {
                    let s = est.lock().unwrap().current_sample();
                    if s.confidence != BandwidthConfidence::None {
                        results.push(s);
                    }
                }
            }
        }
        results.sort_by(|a, b| b.available_bps.cmp(&a.available_bps));
        results
    }

    /// Recommend the best transport for a payload of `payload_bytes`.
    ///
    /// Scoring (higher = better):
    /// 1. `score = (available_bps / power_cost) × bdp_bonus × confidence_factor`
    /// 2. `bdp_bonus = 1.5` when payload fits in BDP, else 1.0.
    /// 3. `confidence_factor = 0.5` for `None` confidence, else 1.0.
    pub fn recommend_transport(&self, peer_uhid: &str, payload_bytes: i64) -> Option<String> {
        let candidates = self.get_estimates(peer_uhid);
        if candidates.is_empty() {
            // No measurement data yet — fall back to lowest power-cost estimator.
            let g = self.estimators.lock().unwrap();
            return g
                .keys()
                .min_by(|a, b| {
                    default_power_cost(a)
                        .partial_cmp(&default_power_cost(b))
                        .unwrap_or(std::cmp::Ordering::Equal)
                })
                .cloned();
        }

        let mut best: Option<String> = None;
        let mut best_score = f64::NEG_INFINITY;

        for s in &candidates {
            let power_cost = default_power_cost(&s.transport_name);
            let available = s.available_bps as f64;
            let bdp_bonus = if payload_bytes <= s.bdp_bytes { 1.5 } else { 1.0 };
            let confidence_factor = if s.confidence == BandwidthConfidence::None {
                0.5
            } else {
                1.0
            };
            let score = (available / power_cost) * bdp_bonus * confidence_factor;
            if score > best_score {
                best_score = score;
                best = Some(s.transport_name.clone());
            }
        }
        best
    }

    // ── Gossip ────────────────────────────────────────────────────────────────

    /// Build a gossip payload for a new peer that has just completed handshake.
    /// Returns `None` if the estimator has no confident estimate yet.
    pub fn build_gossip_payload(
        &self,
        peer_uhid: &str,
        transport_name: &str,
    ) -> Option<BandwidthGossipPayload> {
        let est = self.estimators.lock().unwrap().get(transport_name).cloned()?;
        let sample = est.lock().unwrap().current_sample();
        if sample.confidence == BandwidthConfidence::None {
            return None;
        }
        Some(BandwidthGossipPayload {
            peer_uhid: peer_uhid.to_string(),
            transport_name: transport_name.to_string(),
            btl_bw_bps: sample.btl_bw_bps,
            rt_prop_us: sample.rt_prop.as_micros() as i64,
            confidence: sample.confidence,
            measured_at: sample.measured_at,
        })
    }

    /// Receive and apply a gossip payload from a remote peer.
    pub fn apply_gossip(&self, payload: BandwidthGossipPayload) {
        let est = {
            let g = self.estimators.lock().unwrap();
            g.get(&payload.transport_name).cloned()
        };
        if let Some(arc) = est {
            let rt_prop = std::time::Duration::from_micros(payload.rt_prop_us.max(0) as u64);
            arc.lock()
                .unwrap()
                .warm_from_gossip(payload.btl_bw_bps, rt_prop, payload.confidence);
            // Seed matrix so get_estimate returns something before first probe.
            let sample = arc.lock().unwrap().current_sample();
            self.matrix
                .lock()
                .unwrap()
                .insert((payload.peer_uhid, payload.transport_name), sample);
        }
    }
}

impl Default for BandwidthDirector {
    fn default() -> Self {
        Self::new()
    }
}
