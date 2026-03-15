// SPDX-License-Identifier: MIT

use serde::{Deserialize, Serialize};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

/// Node capabilities bitfield
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Capabilities(u8);

impl Capabilities {
    pub const BLE: u8 = 1;
    pub const WIFI_DIRECT: u8 = 2;
    pub const GATEWAY: u8 = 4;
    pub const RELAY: u8 = 8;
    pub const SOS: u8 = 16;
    pub const STREAMING: u8 = 32;
    pub const VOICE: u8 = 64;
    pub const DTN_CARRIER: u8 = 128;

    pub fn new(value: u8) -> Self {
        Capabilities(value)
    }

    pub fn value(&self) -> u8 {
        self.0
    }

    pub fn has(&self, capability: u8) -> bool {
        (self.0 & capability) != 0
    }

    pub fn set(&mut self, capability: u8) {
        self.0 |= capability;
    }

    pub fn clear(&mut self, capability: u8) {
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
    pub capabilities: u8,
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
        }
    }
}

/// A route entry in the routing table
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RouteEntry {
    pub destination_uhid: String,
    pub next_hop_uhid: String,
    pub hop_count: u32,
    pub quality_score: u8,
    pub expires_at: u64,
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

/// Pre-key bundle for session establishment
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PreKeyBundle {
    pub uhid: String,
    pub identity_key: Vec<u8>,
    pub pre_key_id: i32,
    pub pre_key: Vec<u8>,
    pub signed_pre_key_id: i32,
    pub signed_pre_key: Vec<u8>,
    pub signed_pre_key_signature: Vec<u8>,
}

impl PreKeyBundle {
    pub fn new(
        uhid: String,
        identity_key: Vec<u8>,
        pre_key_id: i32,
        pre_key: Vec<u8>,
        signed_pre_key_id: i32,
        signed_pre_key: Vec<u8>,
        signed_pre_key_signature: Vec<u8>,
    ) -> Self {
        PreKeyBundle {
            uhid,
            identity_key,
            pre_key_id,
            pre_key,
            signed_pre_key_id,
            signed_pre_key,
            signed_pre_key_signature,
        }
    }
}

/// Signal Protocol session state
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
        }
    }
}

/// Encrypted payload for Signal Protocol messages
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EncryptedPayload {
    pub ciphertext: Vec<u8>,
    pub nonce: Vec<u8>,
    pub message_type: i32,
    pub sender_uhid: String,
    pub counter: u32,
    pub encrypted_at: u64,
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
        }
    }
}

/// DTN Bundle for store-and-forward delivery
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DtnBundle {
    pub id: Uuid,
    pub sender_uhid: String,
    pub recipient_uhid: String,
    pub encrypted_payload: Vec<u8>,
    pub priority: u8,
    pub status: u8,
    pub copy_count: i32,
    pub max_copies: i32,
    pub hop_count: i32,
    pub created_at: u64,
    pub expires_at: u64,
}

impl DtnBundle {
    pub fn new(
        sender_uhid: String,
        recipient_uhid: String,
        encrypted_payload: Vec<u8>,
        priority: u8,
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
            status: 0, // Pending
            copy_count: 1,
            max_copies: 3,
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
