// SPDX-License-Identifier: MIT

//! Production [`RelayLink`] that carries circuit-relay-v2 frames one hop over the real
//! mesh — mirrors the C# `MeshRelayLink` and the Go / Python / TS `MeshRelayLink`.
//!
//! Each frame is wrapped in a [`crate::protocol::MeshPacket`] of type
//! [`crate::protocol::PacketType::CircuitRelayControl`] and handed to the host's
//! send-to-connected-peer closure; inbound CircuitRelayControl packets are fed back into
//! the engine via [`MeshRelayLink::handle_incoming_packet`]. The two closures are the seam
//! to whatever real transport the host runs (BLE / Wi-Fi Direct / WebRTC / the HTTP relay).
//! It never calls a radio directly and never recurses through itself (the host's one-hop
//! send must exclude the circuit-relay transport).

use std::sync::Mutex;

use crate::protocol::{MeshPacket, PacketType};

use super::RelayLink;

/// Host closure that sends a [`MeshPacket`] one hop to a directly-connected peer.
pub type SendOneHop = Box<dyn Fn(MeshPacket) -> bool + Send + Sync>;
/// Host closure reporting whether this node has a direct one-hop link to a peer.
pub type CanReachFn = Box<dyn Fn(&str) -> bool + Send + Sync>;

/// A mesh-backed [`RelayLink`]; see the module docs.
pub struct MeshRelayLink {
    local_uhid: String,
    send_one_hop: SendOneHop,
    can_reach: CanReachFn,
    handler: Mutex<Option<Box<dyn Fn(String, Vec<u8>) + Send + Sync>>>,
}

impl MeshRelayLink {
    /// * `local_uhid` — this node's UHID (stamped as the packet source).
    /// * `send_one_hop` — sends a MeshPacket to a directly-connected peer; `true` if handed off.
    /// * `can_reach` — reports whether this node has a direct one-hop link to a peer.
    pub fn new(local_uhid: impl Into<String>, send_one_hop: SendOneHop, can_reach: CanReachFn) -> Self {
        MeshRelayLink {
            local_uhid: local_uhid.into(),
            send_one_hop,
            can_reach,
            handler: Mutex::new(None),
        }
    }

    /// Feeds an inbound CircuitRelayControl packet from the host's receive path into the relay
    /// engine (non-relay packet types are ignored). The host must call this for every received
    /// [`PacketType::CircuitRelayControl`] packet.
    pub fn handle_incoming_packet(&self, packet: &MeshPacket) {
        if packet.packet_type != PacketType::CircuitRelayControl {
            return;
        }
        if let Some(h) = self.handler.lock().unwrap().as_ref() {
            h(packet.source_uhid.clone(), packet.payload.clone());
        }
    }
}

impl RelayLink for MeshRelayLink {
    fn send_frame(&self, node: &str, frame: &[u8]) -> bool {
        let mut pkt = MeshPacket::new(PacketType::CircuitRelayControl, self.local_uhid.clone());
        pkt.destination_uhid = node.to_string();
        pkt.payload = frame.to_vec();
        pkt.ttl = 1; // relay frames travel exactly one hop; end-to-end routing is the engine's job
        (self.send_one_hop)(pkt)
    }

    fn can_reach(&self, node: &str) -> bool {
        (self.can_reach)(node)
    }

    fn set_on_frame(&self, handler: Box<dyn Fn(String, Vec<u8>) + Send + Sync>) {
        *self.handler.lock().unwrap() = Some(handler);
    }
}
