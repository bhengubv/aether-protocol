// SPDX-License-Identifier: MIT

pub mod in_process;
pub mod manager;
pub mod predictive_selector;
pub mod rlnc;
#[cfg(feature = "webrtc")]
pub mod webrtc;
#[cfg(feature = "lora")]
pub mod lora;

use async_trait::async_trait;
use std::sync::{Arc, Mutex};

pub use in_process::InProcessTransport;
pub use manager::TransportManager;
pub use predictive_selector::{PredictedRankedTransport, PredictiveTransportSelector};
#[cfg(feature = "webrtc")]
pub use webrtc::{
    InMemorySignalingBus, Signal, SignalType, Signaling, WebRtcTransport,
};
#[cfg(feature = "lora")]
pub use lora::{LoRaOptions, LoRaSerialTransport};

// ── PerTransportMetrics ───────────────────────────────────────────────────────

const ALPHA: f64 = 0.10; // EWMA smoothing factor (matches C# reference impl)

/// Thread-safe EWMA metrics for one transport link.
///
/// Conservative initial priors ensure unobserved transports rank below
/// any transport that has actual measurements.
///
/// α = 0.10: the most-recent sample contributes 10%; older history decays
/// by 0.90 per observation — deliberately more stable than Python's 0.20
/// because the Rust implementation is used with the Kalman selector which
/// tracks its own RTT state; EWMA here focuses on loss and throughput.
pub struct PerTransportMetrics {
    inner: Mutex<MetricsInner>,
}

struct MetricsInner {
    sample_count:        u64,
    ewma_rtt_ms:         f64,
    ewma_loss_rate:      f64,
    ewma_throughput_bps: f64,
}

impl PerTransportMetrics {
    /// Create a new instance wrapped in an `Arc` ready for shared ownership.
    ///
    /// Initial priors:
    ///   - ewma_rtt_ms   = 200 ms (conservative — unknown link)
    ///   - ewma_loss_rate = 0.05  (5 % assumed until we see real packets)
    ///   - ewma_throughput_bps = 0 (bootstrapped on first successful sample)
    pub fn new() -> Arc<Self> {
        Arc::new(PerTransportMetrics {
            inner: Mutex::new(MetricsInner {
                sample_count:        0,
                ewma_rtt_ms:         200.0,
                ewma_loss_rate:      0.05,
                ewma_throughput_bps: 0.0,
            }),
        })
    }

    /// Record one send observation — updates all three EWMA values.
    ///
    /// # Arguments
    /// * `rtt_ms`            – Measured RTT in ms; 0 = one-way / unknown.
    /// * `success`           – Whether the peer acknowledged receipt.
    /// * `bytes_transferred` – Payload bytes on wire; 0 skips throughput update.
    pub fn record_sample(&self, rtt_ms: u64, success: bool, bytes_transferred: u64) {
        let mut g = self.inner.lock().unwrap();
        g.sample_count += 1;

        if rtt_ms > 0 {
            g.ewma_rtt_ms = ALPHA * rtt_ms as f64 + (1.0 - ALPHA) * g.ewma_rtt_ms;
        }

        let loss_obs: f64 = if success { 0.0 } else { 1.0 };
        g.ewma_loss_rate = ALPHA * loss_obs + (1.0 - ALPHA) * g.ewma_loss_rate;

        if success && rtt_ms > 0 && bytes_transferred > 0 {
            let tput_bps = bytes_transferred as f64 * 8.0 * 1_000.0 / rtt_ms as f64;
            if g.ewma_throughput_bps < 1.0 {
                // Bootstrap from zero.
                g.ewma_throughput_bps = tput_bps;
            } else {
                g.ewma_throughput_bps =
                    ALPHA * tput_bps + (1.0 - ALPHA) * g.ewma_throughput_bps;
            }
        }
    }

    /// EWMA packet-loss rate in \[0, 1\] (lower = better).
    pub fn ewma_loss_rate(&self) -> f64 {
        self.inner.lock().unwrap().ewma_loss_rate
    }

    /// EWMA throughput in bits per second (higher = better).
    pub fn ewma_throughput_bps(&self) -> f64 {
        self.inner.lock().unwrap().ewma_throughput_bps
    }

    /// EWMA round-trip time in milliseconds (lower = better).
    pub fn ewma_rtt_ms(&self) -> f64 {
        self.inner.lock().unwrap().ewma_rtt_ms
    }
}

// ── TransportService ──────────────────────────────────────────────────────────

/// Trait for transport layer implementations.
#[async_trait]
pub trait TransportService: Send + Sync {
    /// Human-readable name of the transport.
    fn name(&self) -> &str;

    /// Whether the transport is currently available.
    fn is_available(&self) -> bool;

    /// Maximum bandwidth in bytes per second.
    fn max_bandwidth_bps(&self) -> i64;

    /// Maximum range in meters.
    fn max_range_meters(&self) -> i32;

    /// Relative power cost (1 = low, 10 = high).
    fn power_cost_relative(&self) -> i32;

    /// Maximum concurrent peers.
    fn max_concurrent_peers(&self) -> i32;

    /// Sends data to a specific peer.
    async fn send_async(
        &self,
        peer_uhid: &str,
        data: &[u8],
    ) -> Result<bool, Box<dyn std::error::Error>>;

    /// Sends a stream to a specific peer.
    async fn send_stream_async(
        &self,
        peer_uhid: &str,
        stream: &mut (dyn std::io::Read + Send + Unpin),
    ) -> Result<bool, Box<dyn std::error::Error>>;

    /// Checks if a connection is active to a peer.
    fn is_connected(&self, peer_uhid: &str) -> bool;

    /// Registers a callback for received data.
    fn set_data_received_handler(
        &mut self,
        handler: Box<dyn Fn(&str, &[u8]) + Send + Sync>,
    );

    /// Registers a received-data callback on the *shared* (`&self`) handle, for transports held
    /// behind an `Arc<dyn TransportService>` (as [`TransportManager`] holds them). Transports with
    /// an interior-mutable receive surface (the circuit relay, WebRTC) override this to store the
    /// handler; the default returns `false`, signalling the transport can only have a handler set
    /// via the `&mut self` [`Self::set_data_received_handler`] before it is shared.
    ///
    /// This is a pure in-process API seam — it carries no wire bytes and is unrelated to any
    /// fixture. It is how [`TransportManager`] subscribes to inbound data generically, without
    /// downcasting to a concrete transport type.
    fn set_shared_data_handler(&self, _handler: Arc<dyn Fn(&str, &[u8]) + Send + Sync>) -> bool {
        false
    }

    /// Per-transport EWMA metrics.
    ///
    /// Returns `None` for transports that do not track metrics (e.g. the
    /// in-process test transport).  The `PredictiveTransportSelector` reads
    /// this to feed the loss-rate and throughput EWMAs alongside its own
    /// Kalman RTT filter.
    fn metrics(&self) -> Option<Arc<PerTransportMetrics>> {
        None
    }
}
