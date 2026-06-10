// SPDX-License-Identifier: MIT

//! BBRv3-inspired per-transport bandwidth estimator.
//!
//! Ports `src/AetherNet.Transport/Bandwidth/BandwidthEstimator.cs`.

use std::collections::VecDeque;
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use super::models::{BandwidthConfidence, BandwidthProbeAck, BandwidthSample};

// ── Constants ─────────────────────────────────────────────────────────────────

/// Number of delivery-rate samples kept in the BtlBw max-filter window.
pub const BTL_BW_WINDOW_SIZE: usize = 10;

/// Minimum RTT window duration in milliseconds (BBRv3 ProbeRTT period).
pub const RT_PROP_WINDOW_MS: f64 = 10_000.0;

/// EWMA loss rate smoothing factor (α).
pub const LOSS_ALPHA: f64 = 0.10;

/// RFC 6298 SRTT smoothing factor (1/8).
const SRTT_ALPHA: f64 = 0.125;

/// RFC 6298 RTTVAR smoothing factor (1/4).
const RTT_VAR_BETA: f64 = 0.25;

/// 5% improvement threshold for the on_sample_improved callback.
const IMPROVEMENT_THRESHOLD: f64 = 0.05;

// ── Inner state ───────────────────────────────────────────────────────────────

struct Inner {
    transport_name: String,
    // BtlBw max-filter: circular buffer of (delivery_rate_bps, timestamp_ms).
    btl_bw_window: [(i64, f64); BTL_BW_WINDOW_SIZE],
    btl_bw_head: usize,
    btl_bw_count: usize,
    // RTprop min-filter
    rt_prop_samples: VecDeque<(f64, f64)>,
    // RFC 6298 state
    srtt_ms: f64,
    rtt_var_ms: f64,
    first_rtt: bool,
    // Loss EWMA
    loss_rate: f64,
    // PHY cap
    phy_cap_bps: i64,
    // Confidence counters
    probe_rounds: u32,
    warmed_from_gossip: bool,
    // Snapshot cache
    current: BandwidthSample,
}

impl Inner {
    fn new(transport_name: &str, max_bandwidth_bps: i64) -> Self {
        let initial = Self::build_snapshot_static(
            transport_name,
            max_bandwidth_bps,
            Duration::from_millis(50),
            1.0,   // srtt_ms
            0.0,   // rtt_var_ms
            0.0,   // loss_rate
            0,     // phy_cap_bps
            BandwidthConfidence::None,
        );
        Self {
            transport_name: transport_name.to_string(),
            btl_bw_window: [(0i64, 0.0f64); BTL_BW_WINDOW_SIZE],
            btl_bw_head: 0,
            btl_bw_count: 0,
            rt_prop_samples: VecDeque::new(),
            srtt_ms: 0.0,
            rtt_var_ms: 0.0,
            first_rtt: true,
            loss_rate: 0.0,
            phy_cap_bps: 0,
            probe_rounds: 0,
            warmed_from_gossip: false,
            current: initial,
        }
    }

    // ── RTT / BtlBw helpers ───────────────────────────────────────────────────

    /// RFC 6298 §2.3 RTT sample integration.
    fn update_rtt_estimates(&mut self, rtt_ms: f64) {
        if self.first_rtt {
            self.srtt_ms = rtt_ms;
            self.rtt_var_ms = rtt_ms / 2.0;
            self.first_rtt = false;
        } else {
            self.rtt_var_ms = (1.0 - RTT_VAR_BETA) * self.rtt_var_ms
                + RTT_VAR_BETA * (self.srtt_ms - rtt_ms).abs();
            self.srtt_ms = (1.0 - SRTT_ALPHA) * self.srtt_ms + SRTT_ALPHA * rtt_ms;
        }
        // Success sample → update loss EWMA (0 loss observed).
        self.loss_rate = LOSS_ALPHA * 0.0 + (1.0 - LOSS_ALPHA) * self.loss_rate;
        let now = now_ms();
        self.add_to_rt_prop_window(rtt_ms, now);
    }

    fn add_to_btl_bw_window(&mut self, rate_bps: i64, now: f64) {
        let window_ms = 10.0 * self.min_rt_prop_ms().max(1.0);
        let expiry = now - window_ms;
        // Evict expired tail entries.
        while self.btl_bw_count > 0 {
            let tail =
                (self.btl_bw_head + BTL_BW_WINDOW_SIZE - self.btl_bw_count) % BTL_BW_WINDOW_SIZE;
            if self.btl_bw_window[tail].1 < expiry {
                self.btl_bw_count -= 1;
            } else {
                break;
            }
        }
        self.btl_bw_window[self.btl_bw_head] = (rate_bps, now);
        self.btl_bw_head = (self.btl_bw_head + 1) % BTL_BW_WINDOW_SIZE;
        if self.btl_bw_count < BTL_BW_WINDOW_SIZE {
            self.btl_bw_count += 1;
        }
    }

    fn add_to_rt_prop_window(&mut self, rtt_ms: f64, now: f64) {
        self.rt_prop_samples.push_back((rtt_ms, now));
        while let Some(&(_, ts)) = self.rt_prop_samples.front() {
            if ts < now - RT_PROP_WINDOW_MS {
                self.rt_prop_samples.pop_front();
            } else {
                break;
            }
        }
    }

    fn max_btl_bw_bps(&self) -> i64 {
        if self.btl_bw_count == 0 {
            return 0;
        }
        let mut max = 0i64;
        for i in 0..self.btl_bw_count {
            let idx =
                (self.btl_bw_head + BTL_BW_WINDOW_SIZE - self.btl_bw_count + i) % BTL_BW_WINDOW_SIZE;
            if self.btl_bw_window[idx].0 > max {
                max = self.btl_bw_window[idx].0;
            }
        }
        max
    }

    fn min_rt_prop_ms(&self) -> f64 {
        if self.rt_prop_samples.is_empty() {
            return if self.srtt_ms > 0.0 { self.srtt_ms } else { 50.0 };
        }
        self.rt_prop_samples
            .iter()
            .map(|&(rtt, _)| rtt)
            .fold(f64::MAX, f64::min)
            .max(1.0)
    }

    fn compute_confidence(&self) -> BandwidthConfidence {
        match self.probe_rounds {
            0 if !self.warmed_from_gossip => BandwidthConfidence::None,
            0 => BandwidthConfidence::Low,
            1..=4 => BandwidthConfidence::Low,
            5..=19 => BandwidthConfidence::Medium,
            _ => BandwidthConfidence::High,
        }
    }

    // ── Snapshot builder ──────────────────────────────────────────────────────

    fn build_snapshot_static(
        transport_name: &str,
        btl_bw: i64,
        rt_prop: Duration,
        srtt_ms: f64,
        rtt_var_ms: f64,
        loss_rate: f64,
        phy_cap_bps: i64,
        confidence: BandwidthConfidence,
    ) -> BandwidthSample {
        let clamped_loss = loss_rate.clamp(0.0, 1.0);
        let srtt = Duration::from_micros((srtt_ms.max(1.0) * 1000.0) as u64);
        let rtt_var = Duration::from_micros((rtt_var_ms.max(0.0) * 1000.0) as u64);
        let effective = if phy_cap_bps > 0 { btl_bw.min(phy_cap_bps) } else { btl_bw };
        let available = (effective as f64 * (1.0 - clamped_loss)) as i64;
        let bdp = if btl_bw > 0 {
            (btl_bw as f64 / 8.0 * rt_prop.as_secs_f64()) as i64
        } else {
            0
        };
        BandwidthSample {
            transport_name: transport_name.to_string(),
            btl_bw_bps: effective,
            available_bps: available,
            bdp_bytes: bdp,
            srtt,
            rtt_var,
            rt_prop,
            loss_rate: clamped_loss,
            phy_cap_bps,
            confidence,
            measured_at: SystemTime::now(),
        }
    }

    /// Rebuild the snapshot and return `Some(sample)` when an improvement should fire.
    fn commit(&mut self) -> Option<BandwidthSample> {
        let prev_btl = self.current.btl_bw_bps;
        let prev_conf = self.current.confidence;

        let btl_bw = self.max_btl_bw_bps();
        let rt_prop = Duration::from_micros((self.min_rt_prop_ms() * 1000.0) as u64);
        let confidence = self.compute_confidence();
        let new_sample = Self::build_snapshot_static(
            &self.transport_name.clone(),
            btl_bw,
            rt_prop,
            self.srtt_ms,
            self.rtt_var_ms,
            self.loss_rate,
            self.phy_cap_bps,
            confidence,
        );

        let new_btl = new_sample.btl_bw_bps;
        let new_conf = new_sample.confidence;
        self.current = new_sample.clone();

        let improved = prev_btl == 0
            || (new_btl - prev_btl) > ((prev_btl as f64 * IMPROVEMENT_THRESHOLD) as i64)
            || new_conf > prev_conf;

        if improved { Some(new_sample) } else { None }
    }
}

// ── Public struct ─────────────────────────────────────────────────────────────

/// BBRv3-inspired per-transport bandwidth estimator.
///
/// Not `Sync` by itself — use internally or wrap in `Arc<Mutex<BandwidthEstimator>>`.
pub struct BandwidthEstimator {
    pub transport_name: String,
    inner: Mutex<Inner>,
    on_sample_improved: Mutex<Option<Box<dyn Fn(&BandwidthSample) + Send + Sync>>>,
}

impl BandwidthEstimator {
    /// Create a new estimator for the named transport.
    /// `max_bandwidth_bps` seeds the initial optimistic estimate with `None` confidence.
    pub fn new(transport_name: &str, max_bandwidth_bps: i64) -> Self {
        Self {
            transport_name: transport_name.to_string(),
            inner: Mutex::new(Inner::new(transport_name, max_bandwidth_bps)),
            on_sample_improved: Mutex::new(None),
        }
    }

    // ── Observation feed ──────────────────────────────────────────────────────

    /// Record a successful delivery of `bytes`.
    /// Both timestamps are microseconds since Unix epoch on the **same clock**.
    pub fn record_delivery(&self, bytes: i32, send_us: i64, deliver_us: i64) {
        if bytes <= 0 || deliver_us <= send_us {
            return;
        }
        let elapsed_ms = (deliver_us - send_us) as f64 / 1000.0;
        let rate_bps = (bytes as f64 * 8.0 / (elapsed_ms / 1000.0)) as i64;
        let rtt_ms = elapsed_ms;
        let sample = {
            let mut g = self.inner.lock().unwrap();
            let now = now_ms();
            g.add_to_btl_bw_window(rate_bps, now);
            g.update_rtt_estimates(rtt_ms);
            g.probe_rounds += 1;
            g.commit()
        };
        self.fire_if_improved(sample);
    }

    /// Record that `bytes` were lost (timeout or explicit NAK).
    pub fn record_loss(&self, bytes: i32) {
        if bytes <= 0 {
            return;
        }
        let sample = {
            let mut g = self.inner.lock().unwrap();
            g.loss_rate = LOSS_ALPHA + (1.0 - LOSS_ALPHA) * g.loss_rate;
            g.commit()
        };
        self.fire_if_improved(sample);
    }

    /// Feed an active probe ACK into the estimator.
    /// `local_receive_us` is the local clock µs at ACK receipt (reserved for future use).
    pub fn record_probe_result(&self, ack: &BandwidthProbeAck, _local_receive_us: i64) {
        let rtt = ack.rtt();
        if rtt == Duration::ZERO || rtt > Duration::from_secs(30) {
            return;
        }
        let rate_bps = if ack.probe_bytes > 0 {
            (ack.probe_bytes as f64 * 8.0 / rtt.as_secs_f64()) as i64
        } else {
            0
        };
        let sample = {
            let mut g = self.inner.lock().unwrap();
            g.update_rtt_estimates(rtt.as_secs_f64() * 1000.0);
            if rate_bps > 0 {
                let now = now_ms();
                g.add_to_btl_bw_window(rate_bps, now);
            }
            g.probe_rounds += 1;
            g.commit()
        };
        self.fire_if_improved(sample);
    }

    /// Pre-warm from a gossip payload.
    /// Only effective when confidence is `None` — never downgrades an existing estimate.
    pub fn warm_from_gossip(
        &self,
        btl_bw_bps: i64,
        rt_prop: Duration,
        _source_confidence: BandwidthConfidence,
    ) {
        let sample = {
            let mut g = self.inner.lock().unwrap();
            if g.probe_rounds > 0 || g.warmed_from_gossip {
                return;
            }
            let now = now_ms();
            g.add_to_btl_bw_window(btl_bw_bps, now);
            let rtt_ms = rt_prop.as_secs_f64() * 1000.0;
            if rtt_ms > 0.0 {
                g.srtt_ms = rtt_ms;
                g.rtt_var_ms = rtt_ms / 2.0;
                g.first_rtt = false;
                g.rt_prop_samples.push_back((rtt_ms, now));
            }
            g.warmed_from_gossip = true;
            g.commit()
        };
        self.fire_if_improved(sample);
    }

    /// Apply a physical-layer RSSI hint. Caps BtlBw on weak radio links.
    pub fn apply_phy_hint(&self, rssi_dbm: i32) {
        let cap: i64 = match rssi_dbm {
            r if r >= -50 => 600_000_000,
            r if r >= -67 => 200_000_000,
            r if r >= -70 =>   2_000_000,
            r if r >= -80 =>  54_000_000,
            r if r >= -85 =>     500_000,
            r if r >= -95 =>     125_000,
            _              =>      40_000,
        };
        let sample = {
            let mut g = self.inner.lock().unwrap();
            g.phy_cap_bps = cap;
            g.commit()
        };
        self.fire_if_improved(sample);
    }

    /// Return an immutable snapshot of the current estimate.
    pub fn current_sample(&self) -> BandwidthSample {
        self.inner.lock().unwrap().current.clone()
    }

    /// Register a callback fired when BtlBw improves by ≥ 5 % or confidence advances.
    pub fn on_sample_improved(&self, cb: Box<dyn Fn(&BandwidthSample) + Send + Sync>) {
        *self.on_sample_improved.lock().unwrap() = Some(cb);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    fn fire_if_improved(&self, maybe: Option<BandwidthSample>) {
        if let Some(ref sample) = maybe {
            let guard = self.on_sample_improved.lock().unwrap();
            if let Some(cb) = guard.as_ref() {
                cb(sample);
            }
        }
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

fn now_ms() -> f64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or(Duration::ZERO)
        .as_secs_f64()
        * 1000.0
}
