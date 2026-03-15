// SPDX-License-Identifier: MIT

use super::TransportService;
use async_trait::async_trait;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

/// In-memory transport for testing and demos
/// Simulates a mesh network using a static registry
pub struct InProcessTransport {
    local_uhid: String,
    network: Arc<Mutex<HashMap<String, Arc<Mutex<Vec<u8>>>>>>,
    data_received_handler: Option<Box<dyn Fn(&str, &[u8]) + Send + Sync>>,
}

impl InProcessTransport {
    /// Creates a new in-process transport node
    pub fn new(local_uhid: String) -> Self {
        InProcessTransport {
            local_uhid,
            network: Arc::new(Mutex::new(HashMap::new())),
            data_received_handler: None,
        }
    }

    /// Registers this node in the simulated network
    pub fn register(&self) -> Result<(), Box<dyn std::error::Error>> {
        let mut net = self.network.lock().map_err(|e| format!("{}", e))?;
        if net.contains_key(&self.local_uhid) {
            return Err("Node already registered".into());
        }
        net.insert(self.local_uhid.clone(), Arc::new(Mutex::new(Vec::new())));
        Ok(())
    }

    /// Unregisters this node from the simulated network
    pub fn unregister(&self) -> Result<(), Box<dyn std::error::Error>> {
        let mut net = self.network.lock().map_err(|e| format!("{}", e))?;
        net.remove(&self.local_uhid);
        Ok(())
    }
}

#[async_trait]
impl TransportService for InProcessTransport {
    fn name(&self) -> &str {
        "InProcess"
    }

    fn is_available(&self) -> bool {
        true
    }

    fn max_bandwidth_bps(&self) -> i64 {
        1_000_000_000 // 1 Gbps
    }

    fn max_range_meters(&self) -> i32 {
        0 // Not applicable for in-process
    }

    fn power_cost_relative(&self) -> i32 {
        0
    }

    fn max_concurrent_peers(&self) -> i32 {
        i32::MAX
    }

    async fn send_async(&self, peer_uhid: &str, data: &[u8]) -> Result<bool, Box<dyn std::error::Error>> {
        if peer_uhid.is_empty() {
            return Ok(false);
        }

        let net = self.network.lock().map_err(|e| format!("{}", e))?;

        if let Some(peer_buffer) = net.get(peer_uhid) {
            let mut buf = peer_buffer.lock().map_err(|e| format!("{}", e))?;
            buf.extend_from_slice(data);
            Ok(true)
        } else {
            Ok(false)
        }
    }

    async fn send_stream_async(
        &self,
        peer_uhid: &str,
        stream: &mut (dyn std::io::Read + Send + Unpin),
    ) -> Result<bool, Box<dyn std::error::Error>> {
        use std::io::Read;

        let mut data = Vec::new();
        stream.read_to_end(&mut data)?;
        self.send_async(peer_uhid, &data).await
    }

    fn is_connected(&self, peer_uhid: &str) -> bool {
        if let Ok(net) = self.network.lock() {
            net.contains_key(peer_uhid)
        } else {
            false
        }
    }

    fn set_data_received_handler(
        &mut self,
        handler: Box<dyn Fn(&str, &[u8]) + Send + Sync>,
    ) {
        self.data_received_handler = Some(handler);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_send_between_nodes() {
        let mut node_a = InProcessTransport::new("node-a".to_string());
        let mut node_b = InProcessTransport::new("node-b".to_string());

        let network = Arc::new(Mutex::new(HashMap::new()));
        node_a.network = network.clone();
        node_b.network = network.clone();

        node_a.register().unwrap();
        node_b.register().unwrap();

        let data = b"test message";
        let result = node_a.send_async("node-b", data).await.unwrap();
        assert!(result);
    }

    #[tokio::test]
    async fn test_send_to_nonexistent_node() {
        let mut node_a = InProcessTransport::new("node-a".to_string());
        node_a.register().unwrap();

        let data = b"test message";
        let result = node_a.send_async("nonexistent", data).await.unwrap();
        assert!(!result);
    }

    #[test]
    fn test_is_connected() {
        let mut node_a = InProcessTransport::new("node-a".to_string());
        let mut node_b = InProcessTransport::new("node-b".to_string());

        let network = Arc::new(Mutex::new(HashMap::new()));
        node_a.network = network.clone();
        node_b.network = network.clone();

        node_a.register().unwrap();
        node_b.register().unwrap();

        assert!(node_a.is_connected("node-b"));
        assert!(!node_a.is_connected("nonexistent"));
    }
}
