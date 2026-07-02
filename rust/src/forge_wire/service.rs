// SPDX-License-Identifier: MIT

//! ForgeAnnounce WIRE binding over [`PacketType::ForgeAnnounce`].
//!
//! Thin transport for the aether-forge package-cache extension: a node broadcasts
//! an announcement when it caches a new package artifact, so mesh peers with the
//! forge capability learn where the artifact lives; inbound announcements surface
//! via a [`ForgeAnnounceReceivedEvent`] (the host records them in its forge
//! service). Mirrors the C# `ForgeAnnounceService` and the Go / Python / TS /
//! Kotlin / Swift ports.

use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tokio::sync::broadcast;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const ANNOUNCE_RECEIVED_CHANNEL_CAPACITY: usize = 64;

/// JSON payload for [`PacketType::ForgeAnnounce`] packets — a freshly-cached
/// artifact announcement.
///
/// Wire format: UTF-8 JSON, snake_case keys, field order `package_id`,
/// `content_hash`, `size_bytes`, `announced_at_ms`, no whitespace, `size_bytes`
/// and `announced_at_ms` bare `i64` integers. Package IDs use a namespaced
/// `ecosystem:name@version` format (e.g. `npm:react@18.2.0`). Byte-identical
/// across all eight language ports — see `fixtures/forge/vectors.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct ForgeAnnounceWire {
    /// Namespaced package identifier (`ecosystem:name@version`).
    package_id: String,
    /// Content hash of the cached artifact.
    content_hash: String,
    /// Artifact size in bytes.
    size_bytes: i64,
    /// Unix timestamp in milliseconds when the artifact was announced.
    announced_at_ms: i64,
}

/// Event emitted when a forge announcement arrives from a peer. Mirrors the C#
/// `AnnounceReceived` event payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ForgeAnnounceReceivedEvent {
    /// Namespaced package identifier.
    pub package_id: String,
    /// Content hash of the cached artifact.
    pub content_hash: String,
    /// Artifact size in bytes.
    pub size_bytes: i64,
    /// Unix-ms timestamp the artifact was announced.
    pub announced_at_ms: i64,
}

/// ForgeAnnounce WIRE service. Broadcasts a freshly-cached artifact announcement
/// and surfaces inbound ones via a [`ForgeAnnounceReceivedEvent`].
pub struct ForgeAnnounceService {
    sender: Arc<dyn MeshSender>,

    /// Broadcast channel for announce-received events. Each subscriber receives an
    /// event the moment an inbound [`PacketType::ForgeAnnounce`] is accepted.
    received_tx: broadcast::Sender<ForgeAnnounceReceivedEvent>,
}

impl ForgeAnnounceService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (received_tx, _) = broadcast::channel(ANNOUNCE_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            received_tx,
        }
    }

    /// Subscribe to announce-received events. Each subscriber receives an event the
    /// moment an inbound [`PacketType::ForgeAnnounce`] is accepted. Best-effort /
    /// fire-and-forget: events are dropped when there are no live receivers.
    pub fn subscribe_received(&self) -> broadcast::Receiver<ForgeAnnounceReceivedEvent> {
        self.received_tx.subscribe()
    }

    /// Announce a cached artifact to mesh peers (`destination_uhid` `*`, TTL
    /// [`DEFAULT_TTL`]). Returns the number of peers reached.
    pub async fn broadcast(
        &self,
        package_id: &str,
        content_hash: &str,
        size_bytes: i64,
        announced_at_ms: i64,
    ) -> usize {
        let body = serde_json::to_vec(&ForgeAnnounceWire {
            package_id: package_id.to_string(),
            content_hash: content_hash.to_string(),
            size_bytes,
            announced_at_ms,
        })
        .unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::ForgeAnnounce, self.sender.local_uhid());
        packet.destination_uhid = "*".to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        self.sender.broadcast(&packet).await
    }

    /// Process an incoming [`PacketType::ForgeAnnounce`] packet: parse it and emit a
    /// [`ForgeAnnounceReceivedEvent`]. Returns `false` for the wrong packet type, a
    /// malformed payload, or an empty package id; `true` once the announcement has
    /// been surfaced.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::ForgeAnnounce {
            return false;
        }

        let body: ForgeAnnounceWire = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };
        if body.package_id.is_empty() {
            return false;
        }

        // Best-effort: deliver to any subscribers. Ignore SendError when there are
        // no live receivers (fire-and-forget).
        let _ = self.received_tx.send(ForgeAnnounceReceivedEvent {
            package_id: body.package_id,
            content_hash: body.content_hash,
            size_bytes: body.size_bytes,
            announced_at_ms: body.announced_at_ms,
        });
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Byte-identity gate: `ForgeAnnounceWire` must serialize to exactly these bytes
    // in every language (fixtures/forge/vectors.json). snake_case keys, field order
    // package_id, content_hash, size_bytes, announced_at_ms, no whitespace,
    // size_bytes/announced_at_ms bare integers. Mirrors the C#
    // `ForgeAnnounce_SerializesToCanonicalBytes`.
    #[test]
    fn forge_announce_payload_serializes_to_canonical_bytes() {
        let announce = ForgeAnnounceWire {
            package_id: "npm:react@18.2.0".to_string(),
            content_hash: "QmForgeHash456".to_string(),
            size_bytes: 294912,
            announced_at_ms: 1_700_000_000_000,
        };
        let json = String::from_utf8(serde_json::to_vec(&announce).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"package_id\":\"npm:react@18.2.0\",\"content_hash\":\"QmForgeHash456\",\
             \"size_bytes\":294912,\"announced_at_ms\":1700000000000}"
        );
    }
}
