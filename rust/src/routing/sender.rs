// SPDX-License-Identifier: MIT

use async_trait::async_trait;

use crate::models::PeerInfo;
use crate::protocol::MeshPacket;

/// Minimal sending abstraction the routing/DTN/SOS services depend on. Hosts
/// wire this with a thin adapter over their transport so this crate doesn't take
/// a hard dependency on a specific transport implementation.
#[async_trait]
pub trait MeshSender: Send + Sync {
    /// The local node's UHID. Used as `MeshPacket::source_uhid` on outbound packets.
    fn local_uhid(&self) -> String;

    /// Local node's last-known geohash, or `None` if not shared.
    fn local_geohash(&self) -> Option<String> {
        None
    }

    /// Snapshot of currently directly-connected peers.
    fn connected_peers(&self) -> Vec<PeerInfo> {
        Vec::new()
    }

    /// Forward a packet to a single next-hop peer (already routed). Returns true if delivered.
    async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool;

    /// Broadcast a packet to every directly connected peer. Returns the fan-out count.
    async fn broadcast(&self, packet: &MeshPacket) -> usize;
}
