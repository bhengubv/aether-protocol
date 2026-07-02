// SPDX-License-Identifier: MIT

//! Directed mesh pre-key exchange over [`PacketType::PreKeyRequest`] (25) and
//! [`PacketType::PreKeyResponse`] (26).
//!
//! A node publishes its current [`PreKeyBundle`] via [`PreKeyExchangeService::set_local_bundle`].
//! A peer asks for it with [`PreKeyExchangeService::request_bundle`]: this mints a request id and
//! directed-sends a `PreKeyRequest`. The responder replies with its published bundle
//! (`PreKeyResponse`); the requester caches it by UHID and surfaces it via a
//! [`PreKeyBundleReceivedEvent`] on a tokio broadcast channel.
//!
//! TRANSPORT ONLY — this is the mesh carriage of bundles. The actual X3DH is performed by the host
//! feeding the received bundle to the Signal service (Signal-canonical: no key agreement here).
//! Mirrors the C# `PreKeyExchangeService` and the Go / Python / TS / Kotlin / Swift ports.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use base64::{engine::general_purpose::STANDARD, Engine as _};
use serde::ser::SerializeStruct;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use tokio::sync::broadcast;
use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::models::PreKeyBundle;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const BUNDLE_RECEIVED_CHANNEL_CAPACITY: usize = 64;

// ─── Wire payloads ──────────────────────────────────────────────────────────

/// JSON payload for [`PacketType::PreKeyRequest`] (25) — a directed ask for a peer's published
/// [`PreKeyBundle`]. Wire: UTF-8 JSON, field order `request_id`, `requester_uhid`, no whitespace,
/// `request_id` a lowercase-dashed UUID. Byte-identity gate: `fixtures/prekey/vectors.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct PreKeyRequestWire {
    /// Correlation id minted by the requester; echoed in the response.
    request_id: Uuid,
    /// UHID of the node asking for the bundle — where the response is sent.
    requester_uhid: String,
}

/// JSON payload for [`PacketType::PreKeyResponse`] (26) — the responder's published
/// [`PreKeyBundle`] carried back to the requester. All public-key material is STANDARD base64
/// (RFC 4648, `+/` alphabet, `=` padding). Field order is pinned by a hand-written [`Serialize`]:
/// `request_id, uhid, identity_key, identity_key_x25519, pre_key_id, pre_key, signed_pre_key_id,
/// signed_pre_key, signed_pre_key_signature`. Byte-identity gate: `fixtures/prekey/vectors.json`.
#[derive(Debug, Clone)]
struct PreKeyResponseWire {
    request_id: Uuid,
    uhid: String,
    identity_key: Vec<u8>,
    identity_key_x25519: Vec<u8>,
    pre_key_id: i32,
    pre_key: Vec<u8>,
    signed_pre_key_id: i32,
    signed_pre_key: Vec<u8>,
    signed_pre_key_signature: Vec<u8>,
}

impl PreKeyResponseWire {
    fn from_bundle(request_id: Uuid, b: &PreKeyBundle) -> Self {
        Self {
            request_id,
            uhid: b.uhid.clone(),
            identity_key: b.identity_key.clone(),
            identity_key_x25519: b.identity_key_x25519.clone(),
            pre_key_id: b.pre_key_id,
            pre_key: b.pre_key.clone(),
            signed_pre_key_id: b.signed_pre_key_id,
            signed_pre_key: b.signed_pre_key.clone(),
            signed_pre_key_signature: b.signed_pre_key_signature.clone(),
        }
    }

    fn into_bundle(self) -> PreKeyBundle {
        PreKeyBundle::new(
            self.uhid,
            self.identity_key,
            self.identity_key_x25519,
            self.pre_key_id,
            self.pre_key,
            self.signed_pre_key_id,
            self.signed_pre_key,
            self.signed_pre_key_signature,
        )
    }
}

// Hand-written Serialize to pin the exact wire field order and STANDARD-base64 the byte fields,
// matching System.Text.Json's default byte[] encoding in the C# reference. serde_json emits
// compact (no whitespace); Uuid's serde impl emits the lowercase-dashed form.
impl Serialize for PreKeyResponseWire {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut s = serializer.serialize_struct("PreKeyResponseWire", 9)?;
        s.serialize_field("request_id", &self.request_id)?;
        s.serialize_field("uhid", &self.uhid)?;
        s.serialize_field("identity_key", &STANDARD.encode(&self.identity_key))?;
        s.serialize_field("identity_key_x25519", &STANDARD.encode(&self.identity_key_x25519))?;
        s.serialize_field("pre_key_id", &self.pre_key_id)?;
        s.serialize_field("pre_key", &STANDARD.encode(&self.pre_key))?;
        s.serialize_field("signed_pre_key_id", &self.signed_pre_key_id)?;
        s.serialize_field("signed_pre_key", &STANDARD.encode(&self.signed_pre_key))?;
        s.serialize_field(
            "signed_pre_key_signature",
            &STANDARD.encode(&self.signed_pre_key_signature),
        )?;
        s.end()
    }
}

// Hand-written Deserialize mirror: the byte fields arrive as STANDARD-base64 strings.
impl<'de> Deserialize<'de> for PreKeyResponseWire {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        #[derive(Deserialize)]
        struct Raw {
            request_id: Uuid,
            uhid: String,
            identity_key: String,
            identity_key_x25519: String,
            pre_key_id: i32,
            pre_key: String,
            signed_pre_key_id: i32,
            signed_pre_key: String,
            signed_pre_key_signature: String,
        }

        let raw = Raw::deserialize(deserializer)?;
        let dec = |s: &str| STANDARD.decode(s).map_err(serde::de::Error::custom);
        Ok(PreKeyResponseWire {
            request_id: raw.request_id,
            uhid: raw.uhid,
            identity_key: dec(&raw.identity_key)?,
            identity_key_x25519: dec(&raw.identity_key_x25519)?,
            pre_key_id: raw.pre_key_id,
            pre_key: dec(&raw.pre_key)?,
            signed_pre_key_id: raw.signed_pre_key_id,
            signed_pre_key: dec(&raw.signed_pre_key)?,
            signed_pre_key_signature: dec(&raw.signed_pre_key_signature)?,
        })
    }
}

// ─── Event ──────────────────────────────────────────────────────────────────

/// Emitted when a peer's pre-key bundle arrives in a [`PacketType::PreKeyResponse`]. Feed
/// [`Self::bundle`] to the Signal service's `process_pre_key_bundle` to complete X3DH. Mirrors the
/// C# `PreKeyBundleReceivedEventArgs`. (No `PartialEq`/`Eq`: [`PreKeyBundle`] does not implement
/// them; tests compare individual fields.)
#[derive(Debug, Clone)]
pub struct PreKeyBundleReceivedEvent {
    /// The request id echoed from the original `PreKeyRequest` (nil if unsolicited).
    pub request_id: Uuid,
    /// UHID of the peer that sent the bundle (the packet source).
    pub from_uhid: String,
    /// The received pre-key bundle.
    pub bundle: PreKeyBundle,
}

// ─── Service ────────────────────────────────────────────────────────────────

/// Directed mesh pre-key exchange service. Directed request/response — never broadcast — so bundle
/// requests do not leak identity-interest to the whole mesh. Transport only: the host wires the
/// published bundle in via [`Self::set_local_bundle`] and consumes received bundles out via the
/// [`PreKeyBundleReceivedEvent`] broadcast.
pub struct PreKeyExchangeService {
    sender: Arc<dyn MeshSender>,

    /// This node's published bundle, served in reply to inbound requests. `None` until set.
    local: Mutex<Option<PreKeyBundle>>,

    /// Most-recently received bundle per peer UHID (cache).
    received: Mutex<HashMap<String, PreKeyBundle>>,

    /// Broadcast channel for bundle-received events. Each subscriber receives an event the moment an
    /// inbound [`PacketType::PreKeyResponse`] is accepted.
    bundle_received_tx: broadcast::Sender<PreKeyBundleReceivedEvent>,
}

impl PreKeyExchangeService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (bundle_received_tx, _) = broadcast::channel(BUNDLE_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            local: Mutex::new(None),
            received: Mutex::new(HashMap::new()),
            bundle_received_tx,
        }
    }

    /// Subscribe to bundle-received events. Each subscriber receives an event the moment an inbound
    /// [`PacketType::PreKeyResponse`] is accepted. Best-effort / fire-and-forget: events are dropped
    /// when there are no live receivers.
    pub fn subscribe_bundle_received(&self) -> broadcast::Receiver<PreKeyBundleReceivedEvent> {
        self.bundle_received_tx.subscribe()
    }

    /// Set (or replace) this node's published bundle — served in reply to inbound requests.
    pub fn set_local_bundle(&self, bundle: PreKeyBundle) {
        *self.local.lock().expect("local bundle mutex poisoned") = Some(bundle);
    }

    /// The currently-published local bundle, or `None` if none has been set.
    pub fn get_local_bundle(&self) -> Option<PreKeyBundle> {
        self.local.lock().expect("local bundle mutex poisoned").clone()
    }

    /// The most recently received bundle for `uhid`, or `None`.
    pub fn get_received_bundle(&self, uhid: &str) -> Option<PreKeyBundle> {
        self.received
            .lock()
            .expect("received bundle mutex poisoned")
            .get(uhid)
            .cloned()
    }

    /// Ask `peer_uhid` for its pre-key bundle: mint a request id and send a directed
    /// [`PacketType::PreKeyRequest`]. Returns the new request id (echoed by the response).
    pub async fn request_bundle(&self, peer_uhid: &str) -> Uuid {
        let request_id = Uuid::new_v4();
        let body = serde_json::to_vec(&PreKeyRequestWire {
            request_id,
            requester_uhid: self.sender.local_uhid(),
        })
        .unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::PreKeyRequest, self.sender.local_uhid());
        packet.destination_uhid = peer_uhid.to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        let _delivered = self.sender.send(&packet, peer_uhid).await;
        request_id
    }

    /// Process an incoming pre-key packet.
    ///
    /// * [`PacketType::PreKeyRequest`] + a local bundle set → directed-send a `PreKeyResponse`
    ///   carrying the local bundle to the requester; returns `true`.
    /// * `PreKeyRequest` + no local bundle → returns `false`, sends nothing.
    /// * [`PacketType::PreKeyResponse`] → cache the bundle by UHID and emit a
    ///   [`PreKeyBundleReceivedEvent`]; returns `true`.
    /// * Any other packet type, a malformed payload, or a response with an empty UHID → `false`.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        match packet.packet_type {
            PacketType::PreKeyRequest => self.handle_request(packet).await,
            PacketType::PreKeyResponse => self.handle_response(packet),
            _ => false,
        }
    }

    async fn handle_request(&self, packet: &MeshPacket) -> bool {
        let body: PreKeyRequestWire = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };

        let local = match self.get_local_bundle() {
            Some(b) => b,
            None => return false,
        };

        let reply_to = if body.requester_uhid.is_empty() {
            packet.source_uhid.clone()
        } else {
            body.requester_uhid.clone()
        };

        let payload =
            serde_json::to_vec(&PreKeyResponseWire::from_bundle(body.request_id, &local))
                .unwrap_or_default();

        let mut reply = MeshPacket::new(PacketType::PreKeyResponse, self.sender.local_uhid());
        reply.destination_uhid = reply_to.clone();
        reply.ttl = DEFAULT_TTL;
        reply.payload = payload;

        let _delivered = self.sender.send(&reply, &reply_to).await;
        true
    }

    fn handle_response(&self, packet: &MeshPacket) -> bool {
        let body: PreKeyResponseWire = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };
        if body.uhid.is_empty() {
            return false;
        }

        let request_id = body.request_id;
        let bundle = body.into_bundle();
        let uhid = bundle.uhid.clone();
        self.received
            .lock()
            .expect("received bundle mutex poisoned")
            .insert(uhid, bundle.clone());

        // Best-effort: deliver to any subscribers. Ignore SendError when there are no live
        // receivers (fire-and-forget), matching the videocall service.
        let _ = self.bundle_received_tx.send(PreKeyBundleReceivedEvent {
            request_id,
            from_uhid: packet.source_uhid.clone(),
            bundle,
        });
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Byte-identity gate: the wire payloads MUST serialize to exactly these bytes in every language
    // (fixtures/prekey/vectors.json). Field order pinned, no whitespace, UUID lowercase-dashed,
    // integer ids bare, every byte[] field STANDARD base64. Mirrors the C#
    // `RequestPayload_SerializesToCanonicalBytes` / `ResponsePayload_SerializesToCanonicalBytes`.

    #[test]
    fn request_payload_serializes_to_canonical_bytes() {
        let payload = PreKeyRequestWire {
            request_id: Uuid::parse_str("11112222-3333-4444-5555-666677778888").unwrap(),
            requester_uhid: "aether:alice:01".to_string(),
        };
        let json = String::from_utf8(serde_json::to_vec(&payload).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"request_id\":\"11112222-3333-4444-5555-666677778888\",\"requester_uhid\":\"aether:alice:01\"}"
        );
    }

    #[test]
    fn response_payload_serializes_to_canonical_bytes() {
        let bundle = PreKeyBundle::new(
            "aether:bob:02".to_string(),
            vec![0x11; 32],
            vec![0x22; 32],
            4242,
            vec![0x33; 32],
            77,
            vec![0x44; 32],
            vec![0x55; 64],
        );
        let payload = PreKeyResponseWire::from_bundle(
            Uuid::parse_str("7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a").unwrap(),
            &bundle,
        );
        let json = String::from_utf8(serde_json::to_vec(&payload).unwrap()).unwrap();
        assert_eq!(
            json,
            "{\"request_id\":\"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a\",\"uhid\":\"aether:bob:02\",\
             \"identity_key\":\"ERERERERERERERERERERERERERERERERERERERERERE=\",\
             \"identity_key_x25519\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\",\
             \"pre_key_id\":4242,\"pre_key\":\"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=\",\
             \"signed_pre_key_id\":77,\"signed_pre_key\":\"REREREREREREREREREREREREREREREREREREREREREQ=\",\
             \"signed_pre_key_signature\":\"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==\"}"
        );
    }

    // Cross-language canonical vectors: assert BOTH expected_json strings from
    // fixtures/prekey/vectors.json byte-for-byte.
    #[test]
    fn matches_shared_fixture_vectors() {
        use std::path::PathBuf;

        let mut root = PathBuf::from(env!("CARGO_MANIFEST_DIR")); // .../aether-protocol/rust
        while !root.join("AetherNetProtocol.slnx").is_file() {
            assert!(root.pop(), "AetherNetProtocol.slnx not found above CARGO_MANIFEST_DIR");
        }
        let vectors_path = root.join("fixtures/prekey/vectors.json");
        let doc: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(&vectors_path).unwrap()).unwrap();

        for v in doc["vectors"].as_array().unwrap() {
            let expected = v["expected_json"].as_str().unwrap();
            let kind = v["kind"].as_str().unwrap();
            let actual = match kind {
                "request" => {
                    let payload = PreKeyRequestWire {
                        request_id: Uuid::parse_str(v["request_id"].as_str().unwrap()).unwrap(),
                        requester_uhid: v["requester_uhid"].as_str().unwrap().to_string(),
                    };
                    String::from_utf8(serde_json::to_vec(&payload).unwrap()).unwrap()
                }
                "response" => {
                    let bundle = PreKeyBundle::new(
                        v["uhid"].as_str().unwrap().to_string(),
                        STANDARD.decode(v["identity_key"].as_str().unwrap()).unwrap(),
                        STANDARD.decode(v["identity_key_x25519"].as_str().unwrap()).unwrap(),
                        v["pre_key_id"].as_i64().unwrap() as i32,
                        STANDARD.decode(v["pre_key"].as_str().unwrap()).unwrap(),
                        v["signed_pre_key_id"].as_i64().unwrap() as i32,
                        STANDARD.decode(v["signed_pre_key"].as_str().unwrap()).unwrap(),
                        STANDARD.decode(v["signed_pre_key_signature"].as_str().unwrap()).unwrap(),
                    );
                    let payload = PreKeyResponseWire::from_bundle(
                        Uuid::parse_str(v["request_id"].as_str().unwrap()).unwrap(),
                        &bundle,
                    );
                    String::from_utf8(serde_json::to_vec(&payload).unwrap()).unwrap()
                }
                other => panic!("unknown vector kind: {other}"),
            };
            assert_eq!(actual, expected, "byte-identity mismatch for vector kind={kind}");
        }
    }
}
