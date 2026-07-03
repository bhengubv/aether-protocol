// SPDX-License-Identifier: MIT

//! Multi-transport manager that routes a packet through the best available transport, falling
//! through to additional transports sorted by power cost — a faithful port of the C#
//! `AetherNet.Transport.Services.TransportManager`.
//!
//! The Rust crate models the *direct* transports (BLE / Wi-Fi Direct / NearLink / CircleLink) as
//! host-platform concerns, so this manager focuses on the cross-language selection contract that
//! the gap-2 acceptance test exercises: a set of [`additional_transports`](TransportManager::new),
//! sorted **ascending** by [`TransportService::power_cost_relative`], each tried in turn until one
//! succeeds. The native circuit relay
//! ([`CircuitRelayTransportService`](crate::circuitrelay::CircuitRelayTransportService), cost 90)
//! therefore lands last — the serverless last-resort fallback — and is selected automatically, not
//! hand-wired.
//!
//! Inbound data surfaces through [`TransportManager::set_data_received`], tagged with the
//! selecting transport's [`name`](TransportService::name) — the manager subscribes to each
//! transport's shared receive surface via
//! [`TransportService::set_shared_data_handler`](crate::transport::TransportService::set_shared_data_handler).

use std::sync::{Arc, Mutex};

use crate::transport::TransportService;

/// Handler shape for data delivered up to the manager's consumer: `(sender_uhid, payload, via)`
/// where `via` is the [`name`](TransportService::name) of the transport that received it.
type ManagerDataHandler = Arc<dyn Fn(&str, &[u8], &str) + Send + Sync>;

/// Routes packets through the best available transport, falling through additional transports in
/// ascending power-cost order. See the module docs.
pub struct TransportManager {
    /// Additional transports, sorted ascending by `power_cost_relative` at construction.
    additional: Vec<Arc<dyn TransportService>>,
    on_data: Arc<Mutex<Option<ManagerDataHandler>>>,
    additional_send_count: Mutex<u64>,
    total_failures: Mutex<u64>,
}

impl TransportManager {
    /// Builds a manager over `additional_transports`, sorted ascending by power cost so the
    /// cheapest is tried first and the relay (cost 90) lands last. The manager immediately
    /// subscribes to each transport's shared receive surface, so inbound data is forwarded to the
    /// handler registered via [`Self::set_data_received`], tagged with the transport's name.
    ///
    /// Mirrors the C# `TransportManager(..., additionalTransports)` constructor (the typed
    /// BLE/Wi-Fi/NearLink/CircleLink parameters are host-platform concerns not modelled here).
    pub fn new(additional_transports: impl IntoIterator<Item = Arc<dyn TransportService>>) -> Arc<Self> {
        let mut additional: Vec<Arc<dyn TransportService>> = additional_transports.into_iter().collect();
        additional.sort_by_key(|t| t.power_cost_relative());

        let on_data: Arc<Mutex<Option<ManagerDataHandler>>> = Arc::new(Mutex::new(None));

        let mgr = Arc::new(TransportManager {
            additional,
            on_data,
            additional_send_count: Mutex::new(0),
            total_failures: Mutex::new(0),
        });

        // Subscribe to each transport's inbound data, tagging it with the transport name — exactly
        // how the C# manager wires `transport.DataReceived += (s, d) => DataReceived(s, d, name)`.
        for t in &mgr.additional {
            let sink = Arc::clone(&mgr.on_data);
            let name = t.name().to_string();
            let _subscribed = t.set_shared_data_handler(Arc::new(move |sender: &str, data: &[u8]| {
                let handler = sink.lock().unwrap().clone();
                if let Some(h) = handler {
                    h(sender, data, &name);
                }
            }));
        }

        mgr
    }

    /// Registers the consumer's received-data callback: `(sender_uhid, payload, via_transport_name)`.
    pub fn set_data_received(&self, handler: ManagerDataHandler) {
        *self.on_data.lock().unwrap() = Some(handler);
    }

    /// Sends `data` to `peer_uhid`, trying each available transport in ascending power-cost order
    /// until one succeeds. Returns `true` on the first success, `false` if every transport failed
    /// or none was available. Mirrors the C# `SendAsync` step 6 (additional transports).
    pub async fn send_async(
        &self,
        peer_uhid: &str,
        data: &[u8],
    ) -> Result<bool, Box<dyn std::error::Error>> {
        for transport in &self.additional {
            if !transport.is_available() {
                continue;
            }
            if transport.send_async(peer_uhid, data).await? {
                *self.additional_send_count.lock().unwrap() += 1;
                return Ok(true);
            }
        }
        *self.total_failures.lock().unwrap() += 1;
        Ok(false)
    }

    /// Number of successful sends routed through an additional transport (diagnostics/tests).
    pub fn additional_send_count(&self) -> u64 {
        *self.additional_send_count.lock().unwrap()
    }

    /// Number of sends that failed on every available transport (diagnostics/tests).
    pub fn total_failures(&self) -> u64 {
        *self.total_failures.lock().unwrap()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use std::sync::atomic::{AtomicUsize, Ordering};

    /// A stub transport that records send attempts and can be told whether to succeed. Its
    /// power cost is configurable so we can prove ascending-cost ordering.
    struct StubTransport {
        name: String,
        power: i32,
        succeed: bool,
        available: bool,
        order: Arc<AtomicUsize>,
        sent_at: AtomicUsize,
        on_data: Arc<Mutex<Option<Arc<dyn Fn(&str, &[u8]) + Send + Sync>>>>,
    }

    impl StubTransport {
        fn new(name: &str, power: i32, succeed: bool, order: Arc<AtomicUsize>) -> Arc<Self> {
            Arc::new(StubTransport {
                name: name.to_string(),
                power,
                succeed,
                available: true,
                order,
                sent_at: AtomicUsize::new(usize::MAX),
                on_data: Arc::new(Mutex::new(None)),
            })
        }
    }

    #[async_trait]
    impl TransportService for StubTransport {
        fn name(&self) -> &str { &self.name }
        fn is_available(&self) -> bool { self.available }
        fn max_bandwidth_bps(&self) -> i64 { 1_000_000 }
        fn max_range_meters(&self) -> i32 { 0 }
        fn power_cost_relative(&self) -> i32 { self.power }
        fn max_concurrent_peers(&self) -> i32 { 8 }
        async fn send_async(&self, _: &str, _: &[u8]) -> Result<bool, Box<dyn std::error::Error>> {
            // Record the global order in which this transport was attempted.
            self.sent_at.store(self.order.fetch_add(1, Ordering::SeqCst), Ordering::SeqCst);
            Ok(self.succeed)
        }
        async fn send_stream_async(&self, _: &str, _: &mut (dyn std::io::Read + Send + Unpin)) -> Result<bool, Box<dyn std::error::Error>> {
            Ok(self.succeed)
        }
        fn is_connected(&self, _: &str) -> bool { false }
        fn set_data_received_handler(&mut self, h: Box<dyn Fn(&str, &[u8]) + Send + Sync>) {
            *self.on_data.lock().unwrap() = Some(Arc::from(h));
        }
        fn set_shared_data_handler(&self, h: Arc<dyn Fn(&str, &[u8]) + Send + Sync>) -> bool {
            *self.on_data.lock().unwrap() = Some(h);
            true
        }
    }

    #[tokio::test]
    async fn tries_transports_in_ascending_power_cost() {
        let order = Arc::new(AtomicUsize::new(0));
        // Keep concrete handles so we can read each stub's recorded attempt order. Register
        // high-cost first to prove the manager re-sorts ascending rather than trusting input order.
        let high = StubTransport::new("high", 90, false, Arc::clone(&order));
        let low = StubTransport::new("low", 10, false, Arc::clone(&order));
        let mgr = TransportManager::new(vec![
            Arc::clone(&high) as Arc<dyn TransportService>,
            Arc::clone(&low) as Arc<dyn TransportService>,
        ]);

        // Both fail → both attempted; the low-cost one must have been attempted first.
        assert!(!mgr.send_async("peer", b"x").await.unwrap());
        assert_eq!(mgr.total_failures(), 1);

        let low_order = low.sent_at.load(Ordering::SeqCst);
        let high_order = high.sent_at.load(Ordering::SeqCst);
        assert!(
            low_order < high_order,
            "low-cost transport (order {low_order}) must be attempted before high-cost (order {high_order})"
        );
    }

    #[tokio::test]
    async fn stops_at_first_success() {
        let order = Arc::new(AtomicUsize::new(0));
        let low: Arc<dyn TransportService> = StubTransport::new("low", 10, true, Arc::clone(&order));
        let high: Arc<dyn TransportService> = StubTransport::new("high", 90, true, Arc::clone(&order));
        let mgr = TransportManager::new(vec![high, low]);

        assert!(mgr.send_async("peer", b"x").await.unwrap());
        assert_eq!(mgr.additional_send_count(), 1);
        assert_eq!(mgr.total_failures(), 0);
    }

    #[tokio::test]
    async fn no_transport_available_fails() {
        let mgr = TransportManager::new(Vec::<Arc<dyn TransportService>>::new());
        assert!(!mgr.send_async("peer", b"x").await.unwrap());
        assert_eq!(mgr.total_failures(), 1);
    }
}
