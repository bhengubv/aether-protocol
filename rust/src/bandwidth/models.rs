// SPDX-License-Identifier: MIT

//! Data models for the AetherNet Bandwidth Measurement Framework (ABMF).
//!
//! Ports `src/AetherNet.Core/Bandwidth/BandwidthModels.cs`.

use std::time::{Duration, SystemTime};

// ── Confidence ────────────────────────────────────────────────────────────────

/// How confident we are in the current bandwidth estimate.
/// Rises with probe rounds; resets on topology change or extended idle.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum BandwidthConfidence {
    None,
    Low,
    Medium,
    High,
}

// ── BandwidthSample ───────────────────────────────────────────────────────────

/// Point-in-time bandwidth measurement for a single transport link.
///
/// Derivation follows BBRv3 (draft-cardwell-iccrg-bbr-congestion-control-02):
/// - `btl_bw_bps` — max delivery rate over 10×RTprop window.
/// - `rt_prop`    — minimum RTT observed in last 10 s (ProbeRTT window).
/// - `srtt`       — RFC 6298 smoothed RTT (α = 1/8).
/// - `rtt_var`    — RFC 6298 mean deviation (β = 1/4).
#[derive(Debug, Clone)]
pub struct BandwidthSample {
    pub transport_name: String,
    /// BBRv3 BtlBw: maximum sustained delivery rate the network can carry (bps).
    pub btl_bw_bps: i64,
    /// Available bandwidth ceiling: BtlBwBps × (1 − loss_rate).
    pub available_bps: i64,
    /// Bandwidth-Delay Product: btl_bw_bps × rt_prop / 8 (bytes).
    pub bdp_bytes: i64,
    /// RFC 6298 smoothed RTT.
    pub srtt: Duration,
    /// RFC 6298 RTT mean deviation (RTTVAR).
    pub rtt_var: Duration,
    /// BBRv3 RTprop: minimum observed RTT over the last 10 seconds.
    pub rt_prop: Duration,
    /// EWMA fractional loss rate [0, 1]; α = 0.10.
    pub loss_rate: f64,
    /// PHY-layer bandwidth cap from RSSI hints (bps). 0 = unknown.
    pub phy_cap_bps: i64,
    pub confidence: BandwidthConfidence,
    pub measured_at: SystemTime,
}

impl BandwidthSample {
    /// RFC 6298 §2.4 RTO: SRTT + max(G, 4×RTTVAR), G = 1 ms clock granularity.
    /// Clamped to [200 ms, 60 s] per §2.4.
    pub fn rto(&self) -> Duration {
        let g_ms = 1.0_f64;
        let srtt_ms = self.srtt.as_secs_f64() * 1000.0;
        let rtt_var_ms = self.rtt_var.as_secs_f64() * 1000.0;
        let raw_ms = srtt_ms + g_ms.max(4.0 * rtt_var_ms);
        let clamped_ms = raw_ms.clamp(200.0, 60_000.0);
        Duration::from_micros((clamped_ms * 1000.0) as u64)
    }

    /// Effective bandwidth: min of btl_bw_bps and phy_cap_bps (if known).
    pub fn effective_bps(&self) -> i64 {
        if self.phy_cap_bps > 0 {
            self.btl_bw_bps.min(self.phy_cap_bps)
        } else {
            self.btl_bw_bps
        }
    }
}

// ── Probe wire models ─────────────────────────────────────────────────────────

/// Four-timestamp probe ACK for two-way delay / RTT measurement (RFC 5136 §3).
/// All timestamps are microseconds since Unix epoch on each peer's local clock.
/// Clock synchronisation is not required — RTT is computed from sender-side timestamps only.
#[derive(Debug, Clone)]
pub struct BandwidthProbeAck {
    pub sequence: u32,
    pub sender_send_us: i64,
    pub receiver_receive_us: i64,
    pub receiver_send_us: i64,
    pub sender_receive_us: i64,
    pub probe_bytes: i32,
}

impl BandwidthProbeAck {
    /// Round-trip time (clock-sync-free).
    /// RTT = (SenderReceive − SenderSend) − receiver processing time.
    pub fn rtt(&self) -> Duration {
        let raw_us = (self.sender_receive_us - self.sender_send_us)
            - (self.receiver_send_us - self.receiver_receive_us);
        if raw_us > 0 {
            Duration::from_micros(raw_us as u64)
        } else {
            Duration::ZERO
        }
    }

    /// Forward one-way delay (sender → receiver). Requires loose clock sync;
    /// treat as approximate unless NTP/PTP is available.
    pub fn forward_owd(&self) -> Duration {
        let raw_us = self.receiver_receive_us - self.sender_send_us;
        if raw_us > 0 {
            Duration::from_micros(raw_us as u64)
        } else {
            Duration::ZERO
        }
    }
}

// ── Gossip warm-start ─────────────────────────────────────────────────────────

/// Gossip payload that a node broadcasts to new peers during handshake.
/// Allows the new session to start with a warm BtlBw estimate instead of
/// probing from zero — unique to AetherNet's mesh topology awareness.
#[derive(Debug, Clone)]
pub struct BandwidthGossipPayload {
    pub peer_uhid: String,
    pub transport_name: String,
    pub btl_bw_bps: i64,
    /// RTprop in microseconds.
    pub rt_prop_us: i64,
    pub confidence: BandwidthConfidence,
    pub measured_at: SystemTime,
}

// ── Node activity (UI layer) ──────────────────────────────────────────────────

/// High-level activity state of a node — suitable for status-bar indicators,
/// dashboard health badges, and connection-quality icons.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NodeActivityState {
    /// No transports available. Node is isolated.
    Offline,
    /// Transports available but no data in the last 5 s.
    Idle,
    /// Data flowing; link utilization < 50 % of estimated capacity.
    Active,
    /// Link utilization ≥ 50 %; performance good but approaching limits.
    Busy,
    /// Loss rate > 5 % or delivery rate declining — likely interference.
    Degraded,
}

/// Activity snapshot for a single transport within the node.
#[derive(Debug, Clone)]
pub struct TransportActivitySnapshot {
    pub transport_name: String,
    pub is_available: bool,
    /// Bytes per second being received on this transport.
    pub ingress_bps: i64,
    /// Bytes per second being sent on this transport.
    pub egress_bps: i64,
    /// Smoothed RTT from the estimator.
    pub srtt: Duration,
    /// Bottleneck bandwidth from the estimator.
    pub btl_bw_bps: i64,
    /// Egress utilization fraction: egress_bps / btl_bw_bps. 0 if btl_bw_bps = 0.
    pub utilization_fraction: f64,
    pub state: NodeActivityState,
    pub confidence: BandwidthConfidence,
}

impl TransportActivitySnapshot {
    /// Human-readable utilization percentage string (e.g. "34 %").
    pub fn utilization_percent(&self) -> String {
        format!("{:.0} %", self.utilization_fraction * 100.0)
    }
}

/// Full node activity snapshot — the top-level model surfaced to UI.
#[derive(Debug, Clone)]
pub struct NodeActivitySnapshot {
    pub state: NodeActivityState,
    /// Aggregate bytes per second flowing INTO this node (all transports).
    pub ingress_bps: i64,
    /// Aggregate bytes per second flowing OUT of this node (all transports).
    pub egress_bps: i64,
    /// Number of remote peers that had traffic in the last 5 s.
    pub active_peers: i32,
    /// Number of transports currently carrying data.
    pub active_transports: i32,
    /// Per-transport breakdown.
    pub transports: Vec<TransportActivitySnapshot>,
    /// Dominant transport: the one carrying the most egress bytes. None if offline or idle.
    pub primary_transport_name: Option<String>,
    pub timestamp: SystemTime,
}

impl NodeActivitySnapshot {
    /// Combined throughput (ingress + egress).
    pub fn total_bps(&self) -> i64 {
        self.ingress_bps + self.egress_bps
    }

    /// True if any transport has data flowing.
    pub fn has_activity(&self) -> bool {
        matches!(
            self.state,
            NodeActivityState::Active | NodeActivityState::Busy | NodeActivityState::Degraded
        )
    }
}
