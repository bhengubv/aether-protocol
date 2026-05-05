// SPDX-License-Identifier: MIT

use serde::{Deserialize, Serialize};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

/// Node capabilities bitfield
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Capabilities(u16);

impl Capabilities {
    pub const BLE: u16 = 1;
    pub const WIFI_DIRECT: u16 = 2;
    pub const GATEWAY: u16 = 4;
    pub const RELAY: u16 = 8;
    pub const SOS: u16 = 16;
    pub const STREAMING: u16 = 32;
    pub const VOICE: u16 = 64;
    pub const DTN_CARRIER: u16 = 128;
    pub const NEAR_LINK: u16 = 256;
    pub const VIDEO: u16 = 512;

    pub fn new(value: u16) -> Self {
        Capabilities(value)
    }

    pub fn value(&self) -> u16 {
        self.0
    }

    pub fn has(&self, capability: u16) -> bool {
        (self.0 & capability) != 0
    }

    pub fn set(&mut self, capability: u16) {
        self.0 |= capability;
    }

    pub fn clear(&mut self, capability: u16) {
        self.0 &= !capability;
    }
}

/// Information about a peer in the mesh network
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerInfo {
    pub uhid: String,
    pub public_key: Vec<u8>,
    pub last_seen: SystemTime,
    pub hop_count: u32,
    pub reliability_score: i32,
    pub capabilities: u16,
    pub geohash: Option<String>,
    pub is_blocked: bool,
}

impl PeerInfo {
    pub fn new(uhid: String, public_key: Vec<u8>) -> Self {
        PeerInfo {
            uhid,
            public_key,
            last_seen: SystemTime::now(),
            hop_count: 0,
            reliability_score: 50,
            capabilities: 0,
            geohash: None,
            is_blocked: false,
        }
    }
}

/// A route entry in the routing table.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RouteEntry {
    pub destination_uhid: String,
    pub next_hop_uhid: String,
    pub hop_count: u32,
    pub quality_score: u8,
    /// Unix-epoch seconds at which this route expires.
    pub expires_at: u64,
    /// Unix-epoch seconds at which this route was last refreshed.
    pub last_updated: u64,
}

impl RouteEntry {
    pub fn new(
        destination_uhid: String,
        next_hop_uhid: String,
        hop_count: u32,
        expiry_seconds: u64,
    ) -> Self {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();

        RouteEntry {
            destination_uhid,
            next_hop_uhid,
            hop_count,
            quality_score: 50,
            expires_at: now + expiry_seconds,
            last_updated: now,
        }
    }

    pub fn is_expired(&self) -> bool {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();
        now >= self.expires_at
    }
}

/// An Aether mesh network node
#[derive(Debug, Clone)]
pub struct AetherNode {
    pub uhid: String,
    pub identity_public_key: Vec<u8>,
    pub identity_private_key: Vec<u8>,
    pub capabilities: Capabilities,
    pub peers: Vec<PeerInfo>,
    pub routes: Vec<RouteEntry>,
}

impl AetherNode {
    pub fn new(uhid: String, identity_public_key: Vec<u8>, identity_private_key: Vec<u8>) -> Self {
        AetherNode {
            uhid,
            identity_public_key,
            identity_private_key,
            capabilities: Capabilities::default(),
            peers: Vec::new(),
            routes: Vec::new(),
        }
    }

    pub fn add_peer(&mut self, peer: PeerInfo) {
        // Remove existing peer with same UHID if present
        self.peers.retain(|p| p.uhid != peer.uhid);
        self.peers.push(peer);
    }

    pub fn get_peer(&self, uhid: &str) -> Option<&PeerInfo> {
        self.peers.iter().find(|p| p.uhid == uhid)
    }

    pub fn add_route(&mut self, route: RouteEntry) {
        // Remove expired and duplicate routes
        self.routes
            .retain(|r| !r.is_expired() && r.destination_uhid != route.destination_uhid);
        self.routes.push(route);
    }

    pub fn get_route(&self, destination_uhid: &str) -> Option<&RouteEntry> {
        self.routes
            .iter()
            .find(|r| r.destination_uhid == destination_uhid && !r.is_expired())
    }

    pub fn cleanup_expired_routes(&mut self) {
        self.routes.retain(|r| !r.is_expired());
    }
}

/// Pre-key bundle published by a node so others can initiate Signal sessions
/// toward it asynchronously.
///
/// Two identity keys per node — Ed25519 for signing and X25519 for ECDH.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PreKeyBundle {
    pub uhid: String,
    /// Long-term Ed25519 identity public key (32 bytes).
    pub identity_key: Vec<u8>,
    /// Long-term X25519 identity public key (32 bytes raw, RFC 7748).
    #[serde(default)]
    pub identity_key_x25519: Vec<u8>,
    pub pre_key_id: i32,
    /// One-time pre-key X25519 public key (32 bytes raw).
    pub pre_key: Vec<u8>,
    pub signed_pre_key_id: i32,
    /// Signed pre-key X25519 public key (32 bytes raw).
    pub signed_pre_key: Vec<u8>,
    /// Ed25519 signature over signed_pre_key (64 bytes).
    pub signed_pre_key_signature: Vec<u8>,
}

impl PreKeyBundle {
    pub fn new(
        uhid: String,
        identity_key: Vec<u8>,
        identity_key_x25519: Vec<u8>,
        pre_key_id: i32,
        pre_key: Vec<u8>,
        signed_pre_key_id: i32,
        signed_pre_key: Vec<u8>,
        signed_pre_key_signature: Vec<u8>,
    ) -> Self {
        PreKeyBundle {
            uhid,
            identity_key,
            identity_key_x25519,
            pre_key_id,
            pre_key,
            signed_pre_key_id,
            signed_pre_key,
            signed_pre_key_signature,
        }
    }
}

/// Signal Protocol session state.
///
/// On the initiator side, `pending_pre_key_message` is true until the first
/// outbound message is sent. While true, the next encrypt() emits a PreKey
/// message carrying the four `initiator_*` fields below.
#[derive(Debug, Clone)]
pub struct SignalSession {
    pub peer_uhid: String,
    pub root_key: Vec<u8>,
    pub send_chain_key: Vec<u8>,
    pub recv_chain_key: Vec<u8>,
    pub send_counter: u32,
    pub recv_counter: u32,
    pub remote_public_key: Vec<u8>,
    pub skipped_message_keys: std::collections::HashMap<u32, Vec<u8>>,
    pub created_at: SystemTime,
    pub updated_at: SystemTime,

    pub pending_pre_key_message: bool,
    pub initiator_identity_key_x25519: Vec<u8>,
    pub initiator_ephemeral_key_x25519: Vec<u8>,
    pub used_signed_pre_key_id: i32,
    pub used_one_time_pre_key_id: i32,
}

impl SignalSession {
    pub fn new(peer_uhid: String, remote_public_key: Vec<u8>) -> Self {
        let now = SystemTime::now();
        SignalSession {
            peer_uhid,
            root_key: Vec::new(),
            send_chain_key: Vec::new(),
            recv_chain_key: Vec::new(),
            send_counter: 0,
            recv_counter: 0,
            remote_public_key,
            skipped_message_keys: std::collections::HashMap::new(),
            created_at: now,
            updated_at: now,
            pending_pre_key_message: false,
            initiator_identity_key_x25519: Vec::new(),
            initiator_ephemeral_key_x25519: Vec::new(),
            used_signed_pre_key_id: 0,
            used_one_time_pre_key_id: 0,
        }
    }
}

/// Wire-level encrypted payload.
///
/// When `message_type` is 1 (PreKey message — the first message from an
/// initiator before a session is established on the responder side), the
/// four `initiator_*` fields carry the data the responder needs to run X3DH
/// on its side. On normal session messages those fields are None/0.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EncryptedPayload {
    pub ciphertext: Vec<u8>,
    pub nonce: Vec<u8>,
    pub message_type: i32,
    pub sender_uhid: String,
    pub counter: u32,
    pub encrypted_at: u64,

    /// PreKey messages: initiator's long-term X25519 identity public key (32 bytes).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub initiator_identity_key_x25519: Option<Vec<u8>>,
    /// PreKey messages: initiator's ephemeral X25519 public key (32 bytes).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub initiator_ephemeral_key_x25519: Option<Vec<u8>>,
    /// PreKey messages: SignedPreKeyId from the recipient bundle the initiator consumed.
    #[serde(default)]
    pub used_signed_pre_key_id: i32,
    /// PreKey messages: one-time PreKeyId from the recipient bundle the initiator consumed.
    #[serde(default)]
    pub used_one_time_pre_key_id: i32,
}

impl EncryptedPayload {
    pub fn new(
        ciphertext: Vec<u8>,
        nonce: Vec<u8>,
        message_type: i32,
        sender_uhid: String,
        counter: u32,
    ) -> Self {
        let encrypted_at = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();

        EncryptedPayload {
            ciphertext,
            nonce,
            message_type,
            sender_uhid,
            counter,
            encrypted_at,
            initiator_identity_key_x25519: None,
            initiator_ephemeral_key_x25519: None,
            used_signed_pre_key_id: 0,
            used_one_time_pre_key_id: 0,
        }
    }
}

/// Lifecycle state of a DTN bundle.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BundleStatus {
    Pending = 0,
    InCustody = 1,
    Delivered = 2,
    Expired = 3,
    Failed = 4,
}

impl BundleStatus {
    pub fn from_u8(value: u8) -> Self {
        match value {
            1 => BundleStatus::InCustody,
            2 => BundleStatus::Delivered,
            3 => BundleStatus::Expired,
            4 => BundleStatus::Failed,
            _ => BundleStatus::Pending,
        }
    }
    pub fn as_u8(&self) -> u8 {
        *self as u8
    }
}

/// Priority class influencing replication aggressiveness.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BundlePriority {
    Low = 0,
    Normal = 1,
    High = 2,
    Sos = 3,
}

impl BundlePriority {
    pub fn from_u8(value: u8) -> Self {
        match value {
            2 => BundlePriority::High,
            3 => BundlePriority::Sos,
            0 => BundlePriority::Low,
            _ => BundlePriority::Normal,
        }
    }
    pub fn as_u8(&self) -> u8 {
        *self as u8
    }
}

/// DTN Bundle for store-and-forward delivery.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DtnBundle {
    pub id: Uuid,
    pub sender_uhid: String,
    pub recipient_uhid: String,
    pub encrypted_payload: Vec<u8>,
    pub priority: BundlePriority,
    pub status: BundleStatus,
    pub copy_count: i32,
    pub max_copies: i32,
    pub sender_geohash: Option<String>,
    pub recipient_last_geohash: Option<String>,
    pub hop_count: i32,
    pub created_at: u64,
    pub expires_at: u64,
}

impl DtnBundle {
    pub fn new(
        sender_uhid: String,
        recipient_uhid: String,
        encrypted_payload: Vec<u8>,
        priority: BundlePriority,
        ttl_hours: u64,
    ) -> Self {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();

        DtnBundle {
            id: Uuid::new_v4(),
            sender_uhid,
            recipient_uhid,
            encrypted_payload,
            priority,
            status: BundleStatus::Pending,
            copy_count: 1,
            max_copies: 3,
            sender_geohash: None,
            recipient_last_geohash: None,
            hop_count: 0,
            created_at: now,
            expires_at: now + (ttl_hours * 3600),
        }
    }

    pub fn is_expired(&self) -> bool {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();
        now >= self.expires_at
    }
}

/// Record of a custody transfer between two nodes.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CustodyRecord {
    pub id: Uuid,
    pub bundle_id: Uuid,
    pub from_uhid: String,
    pub to_uhid: String,
    pub accepted: bool,
    pub transferred_at: u64,
}

/// Receipt sent back to the sender once a bundle is delivered.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DtnDeliveryReceipt {
    pub bundle_id: Uuid,
    pub recipient_uhid: String,
    pub total_hops: i32,
    pub total_custody_transfers: i32,
    pub delivered_at: u64,
}

/// An SOS alert observed on the mesh — locally originated or received.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SosAlert {
    pub id: Uuid,
    pub sender_uhid: String,
    pub broadcast_type: String,
    pub message: Option<String>,
    pub latitude: f64,
    pub longitude: f64,
    pub geohash: Option<String>,
    pub received_at: u64,
}
