// SPDX-License-Identifier: MIT

//! SpaceBreadcrumb WIRE binding over [`PacketType::SpaceBreadcrumb`].
//!
//! Thin transport for the aether-space geo-pinned-noticeboard extension: a node
//! broadcasts a locally-dropped breadcrumb, and inbound breadcrumbs surface via a
//! [`SpaceBreadcrumbReceivedEvent`] (the host pins them into its space service).
//! Mirrors the C# `SpaceBreadcrumbService` and the Go / Python / TS / Kotlin /
//! Swift ports.

use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tokio::sync::broadcast;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const BREADCRUMB_RECEIVED_CHANNEL_CAPACITY: usize = 64;

/// JSON payload for [`PacketType::SpaceBreadcrumb`] packets — projects a breadcrumb
/// onto a byte-identical JSON shape.
///
/// Wire format: UTF-8 JSON, snake_case keys, field order `content_hash`,
/// `geo_hash`, `anchor_uhid`, `created_at_ms`, `ttl_hours`, `type`, `signature`,
/// no whitespace. `created_at_ms` is a bare `i64` Unix-ms integer (not ISO-8601),
/// `ttl_hours` a bare `i32`, `type` a bare `i32` (the breadcrumb category:
/// Notice=0, Emergency=1, Event=3), and `signature` STANDARD base64 (the empty
/// string when unsigned). Byte-identical across all eight language ports — see
/// `fixtures/space/vectors.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct SpaceBreadcrumbWire {
    /// Content hash of the actual payload (text/image/binary), addressed separately.
    content_hash: String,
    /// 6-character geohash of the drop location.
    geo_hash: String,
    /// UHID of the node that dropped the breadcrumb.
    anchor_uhid: String,
    /// Unix timestamp in milliseconds when the breadcrumb was created.
    created_at_ms: i64,
    /// Time-to-live in hours.
    ttl_hours: i32,
    /// Category of the breadcrumb (Notice=0, Emergency=1, Event=3).
    #[serde(rename = "type")]
    crumb_type: i32,
    /// Ed25519 signature over the breadcrumb, STANDARD base64 (empty string if unsigned).
    #[serde(with = "b64_bytes")]
    signature: Vec<u8>,
}

/// STANDARD-base64 (de)serialization for the `signature` byte field: bytes → a
/// base64 string on the wire (an empty `Vec<u8>` → `""`), and back on the way in.
mod b64_bytes {
    use base64::{engine::general_purpose::STANDARD, Engine as _};
    use serde::{Deserialize, Deserializer, Serializer};

    pub fn serialize<S: Serializer>(bytes: &[u8], s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&STANDARD.encode(bytes))
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Vec<u8>, D::Error> {
        let s = String::deserialize(d)?;
        STANDARD.decode(s).map_err(serde::de::Error::custom)
    }
}

/// Event emitted when a breadcrumb arrives from a peer. Carries the wire-decoded
/// breadcrumb fields (the host pins them into its space service). Mirrors the C#
/// `BreadcrumbReceived` event payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SpaceBreadcrumbReceivedEvent {
    /// Content hash of the breadcrumb payload.
    pub content_hash: String,
    /// Geohash of the drop location.
    pub geo_hash: String,
    /// UHID of the node that dropped the breadcrumb.
    pub anchor_uhid: String,
    /// Unix-ms timestamp the breadcrumb was created.
    pub created_at_ms: i64,
    /// Time-to-live in hours.
    pub ttl_hours: i32,
    /// Category of the breadcrumb (Notice=0, Emergency=1, Event=3).
    pub crumb_type: i32,
    /// Ed25519 signature over the breadcrumb (empty if unsigned).
    pub signature: Vec<u8>,
}

/// SpaceBreadcrumb WIRE service. Broadcasts a locally-dropped breadcrumb and
/// surfaces inbound ones via a [`SpaceBreadcrumbReceivedEvent`].
pub struct SpaceBreadcrumbService {
    sender: Arc<dyn MeshSender>,

    /// Broadcast channel for breadcrumb-received events. Each subscriber receives
    /// an event the moment an inbound [`PacketType::SpaceBreadcrumb`] is accepted.
    received_tx: broadcast::Sender<SpaceBreadcrumbReceivedEvent>,
}

impl SpaceBreadcrumbService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (received_tx, _) = broadcast::channel(BREADCRUMB_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            received_tx,
        }
    }

    /// Subscribe to breadcrumb-received events. Each subscriber receives an event
    /// the moment an inbound [`PacketType::SpaceBreadcrumb`] is accepted.
    /// Best-effort / fire-and-forget: events are dropped when there are no live
    /// receivers.
    pub fn subscribe_received(&self) -> broadcast::Receiver<SpaceBreadcrumbReceivedEvent> {
        self.received_tx.subscribe()
    }

    /// Flood a breadcrumb to mesh peers (`destination_uhid` `*`, TTL
    /// [`DEFAULT_TTL`]). Returns the number of peers it was delivered to.
    pub async fn broadcast(
        &self,
        content_hash: &str,
        geo_hash: &str,
        anchor_uhid: &str,
        created_at_ms: i64,
        ttl_hours: i32,
        crumb_type: i32,
        signature: &[u8],
    ) -> usize {
        let body = serde_json::to_vec(&SpaceBreadcrumbWire {
            content_hash: content_hash.to_string(),
            geo_hash: geo_hash.to_string(),
            anchor_uhid: anchor_uhid.to_string(),
            created_at_ms,
            ttl_hours,
            crumb_type,
            signature: signature.to_vec(),
        })
        .unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::SpaceBreadcrumb, self.sender.local_uhid());
        packet.destination_uhid = "*".to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        self.sender.broadcast(&packet).await
    }

    /// Process an incoming [`PacketType::SpaceBreadcrumb`] packet: parse it and emit
    /// a [`SpaceBreadcrumbReceivedEvent`]. Returns `false` for the wrong packet
    /// type, a malformed payload, or an empty content hash; `true` once the
    /// breadcrumb has been surfaced.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::SpaceBreadcrumb {
            return false;
        }

        let body: SpaceBreadcrumbWire = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };
        if body.content_hash.is_empty() {
            return false;
        }

        // Best-effort: deliver to any subscribers. Ignore SendError when there are
        // no live receivers (fire-and-forget).
        let _ = self.received_tx.send(SpaceBreadcrumbReceivedEvent {
            content_hash: body.content_hash,
            geo_hash: body.geo_hash,
            anchor_uhid: body.anchor_uhid,
            created_at_ms: body.created_at_ms,
            ttl_hours: body.ttl_hours,
            crumb_type: body.crumb_type,
            signature: body.signature,
        });
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Byte-identity gate: `SpaceBreadcrumbWire` must serialize to exactly these
    // bytes in every language (fixtures/space/vectors.json). snake_case keys, field
    // order content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type,
    // signature, no whitespace, created_at_ms/ttl_hours/type bare integers,
    // signature STANDARD base64 ("" when unsigned). Mirrors the C#
    // `SpaceBreadcrumb_Emergency_SerializesToCanonicalBytes` +
    // `SpaceBreadcrumb_NoticeUnsigned_SerializesToCanonicalBytes`.
    #[test]
    fn space_breadcrumb_payload_serializes_to_canonical_bytes() {
        // emergency_signed: 64 bytes of 0x99.
        let emergency = SpaceBreadcrumbWire {
            content_hash: "QmContentHashExample123".to_string(),
            geo_hash: "u4pruy".to_string(),
            anchor_uhid: "aether:alice:01".to_string(),
            created_at_ms: 1_700_000_000_000,
            ttl_hours: 720,
            crumb_type: 1,
            signature: vec![0x99u8; 64],
        };
        let json = String::from_utf8(serde_json::to_vec(&emergency).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"content_hash\":\"QmContentHashExample123\",\"geo_hash\":\"u4pruy\",\"anchor_uhid\":\"aether:alice:01\",\
             \"created_at_ms\":1700000000000,\"ttl_hours\":720,\"type\":1,\
             \"signature\":\"mZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmQ==\"}"
        );

        // notice_unsigned: empty signature -> "".
        let notice = SpaceBreadcrumbWire {
            content_hash: "QmNotice777".to_string(),
            geo_hash: "gcpvj0".to_string(),
            anchor_uhid: "aether:bob:02".to_string(),
            created_at_ms: 0,
            ttl_hours: 72,
            crumb_type: 0,
            signature: Vec::new(),
        };
        let json = String::from_utf8(serde_json::to_vec(&notice).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"content_hash\":\"QmNotice777\",\"geo_hash\":\"gcpvj0\",\"anchor_uhid\":\"aether:bob:02\",\
             \"created_at_ms\":0,\"ttl_hours\":72,\"type\":0,\"signature\":\"\"}"
        );
    }
}
