// SPDX-License-Identifier: MIT

//! Heartbeat origination and peer-liveness tracking.
//!
//! A node periodically broadcasts a [`PacketType::Heartbeat`] beacon to its
//! DIRECT neighbours (TTL 1); receivers maintain a per-peer [`PeerLiveness`]
//! table and can query which peers are currently live. Unauthenticated by
//! design — like SOS, a heartbeat is a low-stakes liveness hint, not a security
//! assertion. Mirrors the C# `HeartbeatService` and the Go / Python / TS /
//! Kotlin / Swift ports.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::sync::broadcast;

use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const PEER_SEEN_CHANNEL_CAPACITY: usize = 64;

/// JSON payload for [`PacketType::Heartbeat`] packets. Wire format: UTF-8 JSON
/// with snake_case keys, field order `sequence` then `sent_at_ms`, no
/// whitespace, both values bare integers. Byte-identical across all eight
/// language ports — see `fixtures/heartbeat/vectors.json`.
///
/// The heartbeat's originator is the enclosing packet's `source_uhid`; it is not
/// duplicated in the body. `sequence` lets a receiver detect loss/ordering;
/// `sent_at_ms` lets it gauge freshness.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct HeartbeatPayload {
    /// Monotonic heartbeat sequence number from the sender (starts at 1, increments per beat).
    sequence: i32,
    /// Unix timestamp in milliseconds when the sender emitted this heartbeat.
    sent_at_ms: i64,
}

/// A peer's last observed liveness, maintained on the receiving node as
/// heartbeats arrive. Mirrors the C# `PeerLiveness` and the event payload of the
/// C# `PeerSeen` event.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PeerLiveness {
    /// UHID of the peer this liveness record describes.
    pub uhid: String,
    /// The [`sequence`](HeartbeatPayload) of the most recent heartbeat seen from the peer.
    pub last_sequence: i32,
    /// The peer-stamped `sent_at_ms` of the most recent heartbeat.
    pub last_sent_at_ms: i64,
    /// Local Unix-ms timestamp when the most recent heartbeat was received.
    pub received_at_ms: i64,
}

/// Event emitted when a heartbeat is received from a peer (new or refreshed
/// liveness). Carries the peer's updated [`PeerLiveness`] — the same payload the
/// C# `PeerSeen` event delivers.
pub type PeerSeenEvent = PeerLiveness;

/// Heartbeat service. Broadcasts [`PacketType::Heartbeat`] beacons (TTL 1, one
/// hop) and tracks the liveness of peers from the heartbeats they broadcast.
pub struct HeartbeatService {
    sender: Arc<dyn MeshSender>,
    state: Mutex<HeartbeatState>,

    /// Broadcast channel for peer-seen events. Each subscriber receives an event
    /// the moment a heartbeat from a peer is accepted (new or refreshed).
    peer_seen_tx: broadcast::Sender<PeerSeenEvent>,
}

struct HeartbeatState {
    /// Monotonic sequence number stamped on our own outbound heartbeats.
    sequence: i32,
    /// Last-known liveness of every peer we have ever seen a heartbeat from,
    /// keyed by the peer's source UHID.
    peers: HashMap<String, PeerLiveness>,
}

impl HeartbeatService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (peer_seen_tx, _) = broadcast::channel(PEER_SEEN_CHANNEL_CAPACITY);
        Self {
            sender,
            state: Mutex::new(HeartbeatState {
                sequence: 0,
                peers: HashMap::new(),
            }),
            peer_seen_tx,
        }
    }

    /// Subscribe to peer-seen events. Each subscriber receives an event the
    /// moment a heartbeat from a peer is accepted (new or refreshed liveness).
    /// Best-effort / fire-and-forget: events are dropped when there are no live
    /// receivers.
    pub fn subscribe_peer_seen(&self) -> broadcast::Receiver<PeerSeenEvent> {
        self.peer_seen_tx.subscribe()
    }

    /// Broadcast a single heartbeat to all directly connected peers (TTL 1). The
    /// sequence number increments on every call (starting at 1). Returns the
    /// number of peers the beacon was delivered to.
    pub async fn send_heartbeat(&self) -> usize {
        let seq = {
            let mut state = self.state.lock().unwrap();
            state.sequence += 1;
            state.sequence
        };

        let body = serde_json::to_vec(&HeartbeatPayload {
            sequence: seq,
            sent_at_ms: unix_millis(),
        })
        .unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::Heartbeat, self.sender.local_uhid());
        packet.destination_uhid = "*".to_string();
        packet.ttl = 1; // heartbeats are single-hop: liveness of DIRECT neighbours only
        packet.payload = body;

        self.sender.broadcast(&packet).await
    }

    /// Process an incoming [`PacketType::Heartbeat`] packet: refresh the sender's
    /// liveness record and emit a [`PeerSeenEvent`]. Returns `false` (no-op) for
    /// self-originated heartbeats, the wrong packet type, or a malformed payload;
    /// `true` when a peer's liveness was recorded.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::Heartbeat {
            return false;
        }
        // Ignore our own heartbeat echoed back.
        if packet.source_uhid == self.sender.local_uhid() {
            return false;
        }

        let body: HeartbeatPayload = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };

        let liveness = PeerLiveness {
            uhid: packet.source_uhid.clone(),
            last_sequence: body.sequence,
            last_sent_at_ms: body.sent_at_ms,
            received_at_ms: unix_millis(),
        };
        {
            let mut state = self.state.lock().unwrap();
            state
                .peers
                .insert(packet.source_uhid.clone(), liveness.clone());
        }

        // Best-effort: deliver to any subscribers. Ignore SendError when there
        // are no live receivers (fire-and-forget).
        let _ = self.peer_seen_tx.send(liveness);
        true
    }

    /// Snapshot of every peer this node has ever seen a heartbeat from.
    pub fn get_known_peers(&self) -> Vec<PeerLiveness> {
        let state = self.state.lock().unwrap();
        state.peers.values().cloned().collect()
    }

    /// Peers whose most recent heartbeat was received within the last
    /// `within_seconds` seconds. A negative window pushes the recency horizon
    /// into the future, excluding even a just-seen peer.
    pub fn get_live_peers(&self, within_seconds: i64) -> Vec<PeerLiveness> {
        let cutoff = unix_millis() - within_seconds.saturating_mul(1000);
        let state = self.state.lock().unwrap();
        state
            .peers
            .values()
            .filter(|p| p.received_at_ms >= cutoff)
            .cloned()
            .collect()
    }
}

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}

#[cfg(test)]
mod tests {
    use super::*;

    // Byte-identity gate: `HeartbeatPayload` must serialize to exactly these
    // bytes in every language (fixtures/heartbeat/vectors.json). snake_case
    // keys, field order sequence then sent_at_ms, no whitespace, both values
    // bare integers. Mirrors the C# `HeartbeatPayload_SerializesToCanonicalBytes`.
    #[test]
    fn heartbeat_payload_serializes_to_canonical_bytes() {
        let cases = [
            (1i32, 1_700_000_000_000i64, "{\"sequence\":1,\"sent_at_ms\":1700000000000}"),
            (0i32, 0i64, "{\"sequence\":0,\"sent_at_ms\":0}"),
        ];

        for (sequence, sent_at_ms, expected) in cases {
            let payload = HeartbeatPayload { sequence, sent_at_ms };
            let bytes = serde_json::to_vec(&payload).unwrap();
            let json = String::from_utf8(bytes).unwrap();
            assert_eq!(
                json, expected,
                "byte-identity mismatch for sequence={sequence} sent_at_ms={sent_at_ms}"
            );
        }
    }
}
