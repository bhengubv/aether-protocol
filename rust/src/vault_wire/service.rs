// SPDX-License-Identifier: MIT

//! VaultShardRequest WIRE binding over [`PacketType::VaultShardRequest`].
//!
//! Thin transport for the aether-vault erasure-coded-storage extension: a node
//! asks the mesh for a shard it needs to recover a file, and inbound shard
//! requests surface via a [`VaultShardRequestReceivedEvent`] (the host answers
//! from its vault service if it holds the shard). Mirrors the C#
//! `VaultShardRequestService` and the Go / Python / TS / Kotlin / Swift ports.

use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tokio::sync::broadcast;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const SHARD_REQUESTED_CHANNEL_CAPACITY: usize = 64;

/// JSON payload for [`PacketType::VaultShardRequest`] packets — a request for an
/// erasure-coded shard.
///
/// Wire format: UTF-8 JSON, snake_case keys, field order `shard_hash`,
/// `requester_uhid`, no whitespace. Byte-identical across all eight language
/// ports — see `fixtures/vaultshard/vectors.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct VaultShardRequestWire {
    /// Hash of the shard being requested.
    shard_hash: String,
    /// UHID of the requesting peer.
    requester_uhid: String,
}

/// Event emitted when a peer requests a shard. Mirrors the C# `ShardRequested`
/// event payload (`VaultShardRequest`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VaultShardRequestReceivedEvent {
    /// Hash of the shard being requested.
    pub shard_hash: String,
    /// UHID of the requesting peer.
    pub requester_uhid: String,
}

/// VaultShardRequest WIRE service. Broadcasts a shard request (the requester is
/// this node's local UHID) and surfaces inbound requests via a
/// [`VaultShardRequestReceivedEvent`].
pub struct VaultShardRequestService {
    sender: Arc<dyn MeshSender>,

    /// Broadcast channel for shard-requested events. Each subscriber receives an
    /// event the moment an inbound [`PacketType::VaultShardRequest`] is accepted.
    requested_tx: broadcast::Sender<VaultShardRequestReceivedEvent>,
}

impl VaultShardRequestService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (requested_tx, _) = broadcast::channel(SHARD_REQUESTED_CHANNEL_CAPACITY);
        Self {
            sender,
            requested_tx,
        }
    }

    /// Subscribe to shard-requested events. Each subscriber receives an event the
    /// moment an inbound [`PacketType::VaultShardRequest`] is accepted. Best-effort
    /// / fire-and-forget: events are dropped when there are no live receivers.
    pub fn subscribe_requested(&self) -> broadcast::Receiver<VaultShardRequestReceivedEvent> {
        self.requested_tx.subscribe()
    }

    /// Broadcast a request for `shard_hash` (`destination_uhid` `*`, TTL
    /// [`DEFAULT_TTL`]); the requester is this node's local UHID. Returns the number
    /// of peers reached.
    pub async fn request_shard(&self, shard_hash: &str) -> usize {
        let body = serde_json::to_vec(&VaultShardRequestWire {
            shard_hash: shard_hash.to_string(),
            requester_uhid: self.sender.local_uhid(),
        })
        .unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::VaultShardRequest, self.sender.local_uhid());
        packet.destination_uhid = "*".to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        self.sender.broadcast(&packet).await
    }

    /// Process an incoming [`PacketType::VaultShardRequest`] packet: parse it and
    /// emit a [`VaultShardRequestReceivedEvent`]. Returns `false` for the wrong
    /// packet type, a malformed payload, or an empty shard hash; `true` once the
    /// request has been surfaced.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::VaultShardRequest {
            return false;
        }

        let body: VaultShardRequestWire = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };
        if body.shard_hash.is_empty() {
            return false;
        }

        // Best-effort: deliver to any subscribers. Ignore SendError when there are
        // no live receivers (fire-and-forget).
        let _ = self.requested_tx.send(VaultShardRequestReceivedEvent {
            shard_hash: body.shard_hash,
            requester_uhid: body.requester_uhid,
        });
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Byte-identity gate: `VaultShardRequestWire` must serialize to exactly these
    // bytes in every language (fixtures/vaultshard/vectors.json). snake_case keys,
    // field order shard_hash, requester_uhid, no whitespace. Mirrors the C#
    // `VaultShardRequest_SerializesToCanonicalBytes`.
    #[test]
    fn vault_shard_request_payload_serializes_to_canonical_bytes() {
        let req = VaultShardRequestWire {
            shard_hash: "QmShardHash789".to_string(),
            requester_uhid: "aether:bob:02".to_string(),
        };
        let json = String::from_utf8(serde_json::to_vec(&req).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"shard_hash\":\"QmShardHash789\",\"requester_uhid\":\"aether:bob:02\"}"
        );
    }
}
