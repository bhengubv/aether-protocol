// SPDX-License-Identifier: MIT

pub mod in_process;

use async_trait::async_trait;
pub use in_process::InProcessTransport;

/// Trait for transport layer implementations
#[async_trait]
pub trait TransportService: Send + Sync {
    /// Human-readable name of the transport
    fn name(&self) -> &str;

    /// Whether the transport is currently available
    fn is_available(&self) -> bool;

    /// Maximum bandwidth in bytes per second
    fn max_bandwidth_bps(&self) -> i64;

    /// Maximum range in meters
    fn max_range_meters(&self) -> i32;

    /// Relative power cost (1 = low, 10 = high)
    fn power_cost_relative(&self) -> i32;

    /// Maximum concurrent peers
    fn max_concurrent_peers(&self) -> i32;

    /// Sends data to a specific peer
    async fn send_async(&self, peer_uhid: &str, data: &[u8]) -> Result<bool, Box<dyn std::error::Error>>;

    /// Sends a stream to a specific peer
    async fn send_stream_async(
        &self,
        peer_uhid: &str,
        stream: &mut (dyn std::io::Read + Send + Unpin),
    ) -> Result<bool, Box<dyn std::error::Error>>;

    /// Checks if a connection is active to a peer
    fn is_connected(&self, peer_uhid: &str) -> bool;

    /// Registers a callback for received data
    fn set_data_received_handler(
        &mut self,
        handler: Box<dyn Fn(&str, &[u8]) + Send + Sync>,
    );
}
