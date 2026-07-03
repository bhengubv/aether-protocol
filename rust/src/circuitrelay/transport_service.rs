// SPDX-License-Identifier: MIT

//! Native circuit-relay-v2 as a first-class [`TransportService`] — the adapter that lets
//! the relay [`engine`](super::Transport) be auto-selected by the mesh
//! [`TransportManager`](crate::transport::TransportManager) as the serverless last-resort
//! fallback, exactly next to BLE / Wi-Fi Direct / WebRTC / the HTTP relay.
//!
//! A faithful port of the C# `AetherNet.Transport.CircuitRelay.CircuitRelayTransportService`
//! and the Go `go/circuitrelay.Transport`. Where the C# reference is *both* the engine and the
//! `ITransportService`, the Rust engine ([`super::Transport`]) is deliberately kept as the pure
//! relay state-machine; this thin adapter wraps it and satisfies [`TransportService`]. It adds
//! no wire behaviour — every byte on the wire is still produced by the byte-locked
//! [`super::serialize`] / [`super::RelayFrame`] codec.
//!
//! Cost: [`Self::power_cost_relative`] is **90** — costly (an extra hop through a third node),
//! so it sits just below the HTTP relay's last-resort cost of 100 and is picked *after* every
//! direct transport. `TransportManager` sorts its additional transports ascending by this cost,
//! so the relay lands last.
//!
//! The receive surface follows the crate convention (see
//! [`crate::transport::WebRtcTransport::on_data_received`]): inbound tunnelled DATA is delivered
//! through an interior-mutable handler registered on the shared (`&self`) `Arc` handle via
//! [`Self::on_data_received`]; the engine feeds it through [`super::Transport::set_on_data`].

use std::sync::{Arc, Mutex};

use async_trait::async_trait;

use crate::protocol::MeshPacket;
use crate::transport::TransportService;

use super::{MeshRelayLink, Options, Transport};

/// The relay transport's stable, cross-language name. `TransportManager` tags every byte it
/// receives via this transport with this exact string; the acceptance test asserts on it to
/// prove the manager *selected* the relay rather than being hand-wired.
pub const RELAY_TRANSPORT_NAME: &str = "Circuit Relay (v2)";

/// Relayed traffic is costly (an extra hop through a third node), so it sits just below the HTTP
/// relay's last-resort cost of 100. Mirrors the C# `PowerCostRelative => 90`.
pub const RELAY_POWER_COST: i32 = 90;

/// Handler shape for inbound tunnelled data: `(sender_uhid, payload)`.
type DataHandler = Arc<dyn Fn(&str, &[u8]) + Send + Sync>;

/// Native circuit-relay-v2 transport. Wraps the relay [`engine`](super::Transport) and exposes it
/// as a [`TransportService`] so it slots into the mesh next to the direct transports and is
/// auto-selected by [`TransportManager`](crate::transport::TransportManager) as the last-resort
/// fallback.
///
/// Clone-cheap is *not* offered: the adapter is meant to be held behind an `Arc` (as
/// `Arc<dyn TransportService>`), matching how the C# reference is registered with the manager.
pub struct CircuitRelayTransportService {
    engine: Transport,
    on_data: Arc<Mutex<Option<DataHandler>>>,
    available: Mutex<bool>,
}

impl CircuitRelayTransportService {
    /// Wraps an already-constructed relay `engine` as a [`TransportService`]. The engine's
    /// endpoint-delivery callback is redirected into this adapter's interior-mutable handler, so
    /// tunnelled DATA that arrives for this node surfaces through
    /// [`TransportService::set_data_received_handler`] / [`Self::on_data_received`] — exactly the
    /// receive contract `TransportManager` consumes.
    pub fn new(engine: Transport) -> Arc<Self> {
        let on_data: Arc<Mutex<Option<DataHandler>>> = Arc::new(Mutex::new(None));

        // Bridge the engine's endpoint delivery into our TransportService receive surface.
        let sink = Arc::clone(&on_data);
        engine.set_on_data(Box::new(move |sender, data| {
            let handler = sink.lock().unwrap().clone();
            if let Some(h) = handler {
                h(&sender, &data);
            }
        }));

        Arc::new(CircuitRelayTransportService {
            engine,
            on_data,
            available: Mutex::new(true),
        })
    }

    /// Registers the handler for inbound tunnelled data on the shared (`&self`) handle, since the
    /// transport is held behind an `Arc`. Equivalent to
    /// [`TransportService::set_data_received_handler`] but callable without `&mut self` — the
    /// idiom used by [`crate::transport::WebRtcTransport::on_data_received`].
    pub fn on_data_received(&self, handler: DataHandler) {
        *self.on_data.lock().unwrap() = Some(handler);
    }

    /// Reserves capacity on `relay_uhid` so peers can reach this node through it (target role).
    /// Returns `true` once the relay confirms the reservation. Blocking; call off a runtime
    /// worker (mirrors the C# `ReserveAsync`).
    pub fn reserve(&self, relay_uhid: &str) -> bool {
        self.engine.reserve(relay_uhid)
    }

    /// Records that `dest_uhid` is reachable via relay `relay_uhid`. In production this is
    /// populated from the directory / reservation gossip; tests set it directly. Mirrors the C#
    /// `SetRoute`.
    pub fn set_route(&self, dest_uhid: impl Into<String>, relay_uhid: impl Into<String>) {
        self.engine.set_route(dest_uhid, relay_uhid);
    }

    /// Number of bridges this node is currently servicing as a relay (diagnostics/tests).
    pub fn active_bridge_count(&self) -> usize {
        self.engine.active_bridge_count()
    }

    /// Number of reservations this node is currently holding as a relay (diagnostics/tests).
    pub fn active_reservation_count(&self) -> usize {
        self.engine.active_reservation_count()
    }

    /// The wrapped relay engine, for callers that need the direct role API.
    pub fn engine(&self) -> &Transport {
        &self.engine
    }

    /// Marks the transport unavailable (mirrors the C# `Dispose` flipping `IsAvailable`).
    pub fn mark_unavailable(&self) {
        *self.available.lock().unwrap() = false;
    }
}

#[async_trait]
impl TransportService for CircuitRelayTransportService {
    fn name(&self) -> &str {
        RELAY_TRANSPORT_NAME
    }

    fn is_available(&self) -> bool {
        *self.available.lock().unwrap()
    }

    fn max_bandwidth_bps(&self) -> i64 {
        5_000_000 // relayed path; conservatively below a direct link (matches C#)
    }

    fn max_range_meters(&self) -> i32 {
        0 // internet-scope
    }

    fn power_cost_relative(&self) -> i32 {
        RELAY_POWER_COST
    }

    fn max_concurrent_peers(&self) -> i32 {
        256
    }

    async fn send_async(
        &self,
        peer_uhid: &str,
        data: &[u8],
    ) -> Result<bool, Box<dyn std::error::Error>> {
        // The engine's `send` blocks on the CONNECT/RESERVE response channels, so it must run on
        // a blocking worker rather than a runtime task. Clone the cheap engine handle + owned
        // args into the closure.
        let engine = self.engine.clone();
        let peer = peer_uhid.to_string();
        let payload = data.to_vec();
        let ok = tokio::task::spawn_blocking(move || engine.send(&peer, &payload))
            .await
            .map_err(|e| Box::new(e) as Box<dyn std::error::Error>)?;
        Ok(ok)
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
        self.engine.is_connected(peer_uhid)
    }

    fn set_data_received_handler(
        &mut self,
        handler: Box<dyn Fn(&str, &[u8]) + Send + Sync>,
    ) {
        *self.on_data.lock().unwrap() = Some(Arc::from(handler));
    }

    fn set_shared_data_handler(&self, handler: Arc<dyn Fn(&str, &[u8]) + Send + Sync>) -> bool {
        self.on_data_received(handler);
        true
    }
}

/// Wires a [`CircuitRelayTransportService`] onto a [`MeshRelayLink`] — the Rust equivalent of the
/// C# `MeshCircuitRelay.Create`. The host:
/// 1. registers the returned transport with the mesh —
///    [`TransportManager`](crate::transport::TransportManager) includes it automatically via its
///    `additional_transports`, at [`RELAY_POWER_COST`] 90 (just below the HTTP relay); and
/// 2. routes every received [`PacketType::CircuitRelayControl`](crate::protocol::PacketType)
///    packet to the returned link's
///    [`MeshRelayLink::handle_incoming_packet`](super::MeshRelayLink::handle_incoming_packet).
pub struct MeshCircuitRelay;

impl MeshCircuitRelay {
    /// Creates the relay transport + its mesh link, wired to the host's one-hop send / reachability
    /// closures. `now` returns epoch **milliseconds** (injectable for deterministic
    /// reservation-expiry tests; pass a wall-clock closure in production).
    pub fn create(
        local_uhid: impl Into<String>,
        send_one_hop: Box<dyn Fn(MeshPacket) -> bool + Send + Sync>,
        can_reach: Box<dyn Fn(&str) -> bool + Send + Sync>,
        opts: Options,
        now: Box<dyn Fn() -> i64 + Send + Sync>,
    ) -> (Arc<CircuitRelayTransportService>, Arc<MeshRelayLink>) {
        let local_uhid = local_uhid.into();
        let link = Arc::new(MeshRelayLink::new(local_uhid.clone(), send_one_hop, can_reach));
        let link_dyn: Arc<dyn super::RelayLink> = link.clone();
        let engine = Transport::new(local_uhid, link_dyn, opts, now);
        let transport = CircuitRelayTransportService::new(engine);
        (transport, link)
    }
}
