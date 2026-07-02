// SPDX-License-Identifier: MIT

//! Presence WIRE binding over [`PacketType::PresenceBeacon`] (21) and
//! [`PacketType::PresenceQuery`] (22).
//!
//! A privacy-preserving presence service. A node broadcasts a
//! [`PresenceBeaconPayload`] ("I'm here") advertising its ROTATING `erid` (never
//! the stable UHID), a COARSE geohash (empty when hidden), its capability bitmask,
//! a presence status, and a send timestamp; or a [`PresenceQueryPayload`]
//! ("who's around here?") to solicit beacon replies. Inbound beacons/queries
//! surface via a [`PresenceBeaconReceivedEvent`] / [`PresenceQueryReceivedEvent`]
//! on tokio broadcast channels.
//!
//! TRANSPORT ONLY — the ERID rotation and the geohash coarsening are the host's
//! concern; this service never touches the stable UHID or precise location.
//! Mirrors the C# `PresenceService` and the Go / Python / TS / Kotlin / Swift
//! ports. Byte-identity gate: `fixtures/presence/vectors.json`.

use std::sync::Arc;

use serde::{Deserialize, Serialize};
use tokio::sync::broadcast;
use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const BEACON_RECEIVED_CHANNEL_CAPACITY: usize = 64;
const QUERY_RECEIVED_CHANNEL_CAPACITY: usize = 64;

// ─── Wire payloads ──────────────────────────────────────────────────────────

/// JSON payload for [`PacketType::PresenceBeacon`] (21) — a privacy-preserving
/// "I'm here" broadcast.
///
/// Wire: UTF-8 JSON, snake_case keys, field order `erid`, `geohash`,
/// `capabilities`, `status`, `sent_at_ms`, no whitespace. `capabilities` /
/// `status` are bare `i32` and `sent_at_ms` a bare `i64` (not ISO-8601). The
/// beacon carries the ROTATING erid (never the stable UHID) and a COARSE geohash
/// (empty string = hidden). Byte-identical across all eight language ports — see
/// `fixtures/presence/vectors.json`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PresenceBeaconPayload {
    /// The node's current rotating Ephemeral Routing Id (Crockford base-32). NOT the UHID.
    #[serde(rename = "erid")]
    pub erid: String,
    /// Coarse geohash of the node (host-truncated per privacy level); empty string = hidden.
    #[serde(rename = "geohash")]
    pub geohash: String,
    /// NodeCapabilities bitmask (BLE=1, WifiDirect=2, Gateway=4, Relay=8, …).
    #[serde(rename = "capabilities")]
    pub capabilities: i32,
    /// PresenceStatus value (Unknown=0, Available=1, Busy=2, Away=3, DoNotDisturb=4, Offline=5).
    #[serde(rename = "status")]
    pub status: i32,
    /// Unix timestamp (ms) when the beacon was sent.
    #[serde(rename = "sent_at_ms")]
    pub sent_at_ms: i64,
}

/// JSON payload for [`PacketType::PresenceQuery`] (22) — "who's around here?".
///
/// Wire: UTF-8 JSON, snake_case keys, field order `query_id`, `geohash`, no
/// whitespace, `query_id` a lowercase-dashed UUID. An empty geohash means
/// "anywhere". Byte-identical across all eight language ports — see
/// `fixtures/presence/vectors.json`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PresenceQueryPayload {
    /// Correlation id minted by the querier.
    #[serde(rename = "query_id")]
    pub query_id: Uuid,
    /// Coarse geohash to scope the query (empty = anywhere).
    #[serde(rename = "geohash")]
    pub geohash: String,
}

// ─── Events ─────────────────────────────────────────────────────────────────

/// Emitted when a presence beacon arrives from a peer. Carries the decoded beacon
/// plus the peer's UHID. Mirrors the C# `PresenceBeaconReceived` event args.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PresenceBeaconReceivedEvent {
    /// The inbound presence beacon.
    pub beacon: PresenceBeaconPayload,
    /// UHID of the peer that sent the beacon (the packet source).
    pub from_uhid: String,
}

/// Emitted when a presence query arrives from a peer. Carries the decoded query
/// plus the peer's UHID. Mirrors the C# `PresenceQueryReceived` event args.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PresenceQueryReceivedEvent {
    /// The inbound presence query.
    pub query: PresenceQueryPayload,
    /// UHID of the peer that sent the query (the packet source).
    pub from_uhid: String,
}

// ─── Service ────────────────────────────────────────────────────────────────

/// Presence WIRE service. Broadcasts a beacon (the host builds it with the
/// rotating erid + coarse geohash) or a query, and surfaces inbound
/// beacons/queries via broadcast-channel events.
pub struct PresenceService {
    sender: Arc<dyn MeshSender>,

    /// Broadcast channel for beacon-received events. Each subscriber receives an
    /// event the moment an inbound [`PacketType::PresenceBeacon`] is accepted.
    beacon_received_tx: broadcast::Sender<PresenceBeaconReceivedEvent>,

    /// Broadcast channel for query-received events. Each subscriber receives an
    /// event the moment an inbound [`PacketType::PresenceQuery`] is accepted.
    query_received_tx: broadcast::Sender<PresenceQueryReceivedEvent>,
}

impl PresenceService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (beacon_received_tx, _) = broadcast::channel(BEACON_RECEIVED_CHANNEL_CAPACITY);
        let (query_received_tx, _) = broadcast::channel(QUERY_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            beacon_received_tx,
            query_received_tx,
        }
    }

    /// Subscribe to beacon-received events. Best-effort / fire-and-forget: events
    /// are dropped when there are no live receivers.
    pub fn subscribe_beacon_received(&self) -> broadcast::Receiver<PresenceBeaconReceivedEvent> {
        self.beacon_received_tx.subscribe()
    }

    /// Subscribe to query-received events. Best-effort / fire-and-forget: events
    /// are dropped when there are no live receivers.
    pub fn subscribe_query_received(&self) -> broadcast::Receiver<PresenceQueryReceivedEvent> {
        self.query_received_tx.subscribe()
    }

    /// Broadcast a presence beacon (`destination_uhid` `*`, TTL [`DEFAULT_TTL`]).
    /// Returns the number of peers it was delivered to.
    pub async fn broadcast_beacon(&self, beacon: PresenceBeaconPayload) -> usize {
        let body = serde_json::to_vec(&beacon).unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::PresenceBeacon, self.sender.local_uhid());
        packet.destination_uhid = "*".to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        self.sender.broadcast(&packet).await
    }

    /// Broadcast a presence query for the given (coarse, possibly empty) geohash
    /// (`destination_uhid` `*`, TTL [`DEFAULT_TTL`]). Mints and returns the new
    /// query id.
    pub async fn query(&self, geohash: &str) -> Uuid {
        let query_id = Uuid::new_v4();
        let payload = PresenceQueryPayload {
            query_id,
            geohash: geohash.to_string(),
        };
        let body = serde_json::to_vec(&payload).unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::PresenceQuery, self.sender.local_uhid());
        packet.destination_uhid = "*".to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        let _delivered = self.sender.broadcast(&packet).await;
        query_id
    }

    /// Process an inbound presence packet (beacon or query).
    ///
    /// * [`PacketType::PresenceBeacon`] with a non-empty `erid` → emit a
    ///   [`PresenceBeaconReceivedEvent`]; returns `true`.
    /// * [`PacketType::PresenceQuery`] → emit a [`PresenceQueryReceivedEvent`];
    ///   returns `true`.
    /// * Any other packet type, a malformed payload, or a beacon with an empty
    ///   `erid` → returns `false`.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        match packet.packet_type {
            PacketType::PresenceBeacon => self.handle_beacon(packet),
            PacketType::PresenceQuery => self.handle_query(packet),
            _ => false,
        }
    }

    fn handle_beacon(&self, packet: &MeshPacket) -> bool {
        let beacon: PresenceBeaconPayload = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };
        if beacon.erid.is_empty() {
            return false;
        }

        // Best-effort: deliver to any subscribers. Ignore SendError when there are
        // no live receivers (fire-and-forget).
        let _ = self.beacon_received_tx.send(PresenceBeaconReceivedEvent {
            beacon,
            from_uhid: packet.source_uhid.clone(),
        });
        true
    }

    fn handle_query(&self, packet: &MeshPacket) -> bool {
        let query: PresenceQueryPayload = match serde_json::from_slice(&packet.payload) {
            Ok(q) => q,
            Err(_) => return false,
        };

        // Best-effort: deliver to any subscribers. Ignore SendError when there are
        // no live receivers (fire-and-forget).
        let _ = self.query_received_tx.send(PresenceQueryReceivedEvent {
            query,
            from_uhid: packet.source_uhid.clone(),
        });
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Byte-identity gate: the wire payloads MUST serialize to exactly these bytes
    // in every language (fixtures/presence/vectors.json). Beacon field order erid,
    // geohash, capabilities, status, sent_at_ms; query field order query_id,
    // geohash; no whitespace; capabilities/status/sent_at_ms bare integers; geohash
    // may be ""; query_id a lowercase-dashed UUID. Mirrors the C#
    // `Beacon_Available_SerializesToCanonicalBytes` /
    // `Beacon_HiddenOffline_SerializesToCanonicalBytes` /
    // `Query_SerializesToCanonicalBytes`.

    #[test]
    fn beacon_available_serializes_to_canonical_bytes() {
        let p = PresenceBeaconPayload {
            erid: "3B38HPPFG9JXE37Q".to_string(),
            geohash: "u4pru".to_string(),
            capabilities: 73,
            status: 1,
            sent_at_ms: 1_700_000_000_000,
        };
        let json = String::from_utf8(serde_json::to_vec(&p).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"erid\":\"3B38HPPFG9JXE37Q\",\"geohash\":\"u4pru\",\"capabilities\":73,\"status\":1,\"sent_at_ms\":1700000000000}"
        );
    }

    #[test]
    fn beacon_hidden_offline_serializes_to_canonical_bytes() {
        let p = PresenceBeaconPayload {
            erid: "0Z5BD0HB1Q7W76MY".to_string(),
            geohash: String::new(),
            capabilities: 0,
            status: 5,
            sent_at_ms: 0,
        };
        let json = String::from_utf8(serde_json::to_vec(&p).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"erid\":\"0Z5BD0HB1Q7W76MY\",\"geohash\":\"\",\"capabilities\":0,\"status\":5,\"sent_at_ms\":0}"
        );
    }

    #[test]
    fn query_serializes_to_canonical_bytes() {
        let p = PresenceQueryPayload {
            query_id: Uuid::parse_str("11112222-3333-4444-5555-666677778888").unwrap(),
            geohash: "u4pru".to_string(),
        };
        let json = String::from_utf8(serde_json::to_vec(&p).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"query_id\":\"11112222-3333-4444-5555-666677778888\",\"geohash\":\"u4pru\"}"
        );
    }

    // Cross-language canonical vectors: assert every expected_json string from
    // fixtures/presence/vectors.json byte-for-byte.
    #[test]
    fn matches_shared_fixture_vectors() {
        use std::path::PathBuf;

        let mut root = PathBuf::from(env!("CARGO_MANIFEST_DIR")); // .../aether-protocol/rust
        while !root.join("AetherNetProtocol.slnx").is_file() {
            assert!(root.pop(), "AetherNetProtocol.slnx not found above CARGO_MANIFEST_DIR");
        }
        let vectors_path = root.join("fixtures/presence/vectors.json");
        let doc: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(&vectors_path).unwrap()).unwrap();

        for v in doc["beacon_vectors"].as_array().unwrap() {
            let payload = PresenceBeaconPayload {
                erid: v["erid"].as_str().unwrap().to_string(),
                geohash: v["geohash"].as_str().unwrap().to_string(),
                capabilities: v["capabilities"].as_i64().unwrap() as i32,
                status: v["status"].as_i64().unwrap() as i32,
                sent_at_ms: v["sent_at_ms"].as_i64().unwrap(),
            };
            let actual = String::from_utf8(serde_json::to_vec(&payload).unwrap()).unwrap();
            assert_eq!(
                actual,
                v["expected_json"].as_str().unwrap(),
                "beacon byte-identity mismatch for vector {}",
                v["name"].as_str().unwrap_or("?")
            );
        }

        for v in doc["query_vectors"].as_array().unwrap() {
            let payload = PresenceQueryPayload {
                query_id: Uuid::parse_str(v["query_id"].as_str().unwrap()).unwrap(),
                geohash: v["geohash"].as_str().unwrap().to_string(),
            };
            let actual = String::from_utf8(serde_json::to_vec(&payload).unwrap()).unwrap();
            assert_eq!(
                actual,
                v["expected_json"].as_str().unwrap(),
                "query byte-identity mismatch for vector {}",
                v["name"].as_str().unwrap_or("?")
            );
        }
    }
}
