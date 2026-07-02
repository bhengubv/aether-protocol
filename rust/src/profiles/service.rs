// SPDX-License-Identifier: MIT

//! Directed profile exchange and peer-profile caching.
//!
//! A node sets its own profile and shares it DIRECTED (point-to-point to a
//! chosen peer, TTL [`DEFAULT_TTL`]) over [`PacketType::ProfileSync`] — NOT
//! broadcast, because broadcasting display names to every device in range is
//! exactly the metadata leak the privacy roadmap forbids. Received profiles are
//! cached (keyed by their UHID) and surfaced via a [`ProfileUpdatedEvent`].
//! Mirrors the C# `ProfileService` and the Go / Python / TS / Kotlin / Swift
//! ports.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::sync::broadcast;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const PROFILE_UPDATED_CHANNEL_CAPACITY: usize = 64;

/// JSON payload for [`PacketType::ProfileSync`] packets. Wire format: UTF-8 JSON
/// with snake_case keys, field order `uhid`, `display_name`, `avatar_ref`,
/// `status_message`, `updated_at_ms`, no whitespace, `updated_at_ms` a bare
/// integer, all string fields always present (empty when unset) — no nulls, so
/// the encoding cannot diverge across languages. Byte-identical across all eight
/// language ports — see `fixtures/profiles/vectors.json`.
///
/// **Privacy:** a profile is exchanged directed (point-to-point to a specific
/// peer), not broadcast. A peer you interact with learns your profile; strangers
/// do not.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProfileSyncPayload {
    /// UHID this profile describes (the sender). Self-identifying so a cached
    /// profile stays attributable.
    pub uhid: String,
    /// Human-readable display name (empty if unset).
    pub display_name: String,
    /// Content-addressed reference to an avatar (e.g. "blake3:…"), empty if none.
    pub avatar_ref: String,
    /// Free-text status / presence message (empty if unset).
    pub status_message: String,
    /// Unix timestamp in milliseconds when the profile was last updated by its owner.
    pub updated_at_ms: i64,
}

/// Event emitted when a peer's profile is received or refreshed. Carries the
/// peer's [`ProfileSyncPayload`] — the same payload the C# `ProfileUpdated`
/// event delivers.
pub type ProfileUpdatedEvent = ProfileSyncPayload;

/// Profile service. Shares this node's profile directly with a chosen peer and
/// caches profiles received from peers. Directed (not broadcast) to avoid leaking
/// identity metadata to the whole mesh.
pub struct ProfileService {
    sender: Arc<dyn MeshSender>,
    state: Mutex<ProfileState>,

    /// Broadcast channel for profile-updated events. Each subscriber receives an
    /// event the moment a peer's profile is received or refreshed.
    profile_updated_tx: broadcast::Sender<ProfileUpdatedEvent>,
}

struct ProfileState {
    /// This node's own profile.
    local: ProfileSyncPayload,
    /// Cached profiles received from peers, keyed by the peer's UHID.
    peer_profiles: HashMap<String, ProfileSyncPayload>,
}

impl ProfileService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (profile_updated_tx, _) = broadcast::channel(PROFILE_UPDATED_CHANNEL_CAPACITY);
        let local = ProfileSyncPayload {
            uhid: sender.local_uhid(),
            display_name: String::new(),
            avatar_ref: String::new(),
            status_message: String::new(),
            updated_at_ms: 0,
        };
        Self {
            sender,
            state: Mutex::new(ProfileState {
                local,
                peer_profiles: HashMap::new(),
            }),
            profile_updated_tx,
        }
    }

    /// Subscribe to profile-updated events. Each subscriber receives an event the
    /// moment a peer's profile is received or refreshed. Best-effort /
    /// fire-and-forget: events are dropped when there are no live receivers.
    pub fn subscribe_profile_updated(&self) -> broadcast::Receiver<ProfileUpdatedEvent> {
        self.profile_updated_tx.subscribe()
    }

    /// Set this node's own profile (stamps `updated_at_ms` to now).
    pub fn set_local_profile(&self, display_name: &str, avatar_ref: &str, status_message: &str) {
        let mut state = self.state.lock().unwrap();
        state.local = ProfileSyncPayload {
            uhid: self.sender.local_uhid(),
            display_name: display_name.to_string(),
            avatar_ref: avatar_ref.to_string(),
            status_message: status_message.to_string(),
            updated_at_ms: unix_millis(),
        };
    }

    /// This node's current local profile.
    pub fn get_local_profile(&self) -> ProfileSyncPayload {
        let state = self.state.lock().unwrap();
        state.local.clone()
    }

    /// Send this node's local profile directly to `peer_uhid` via the sender's
    /// directed send (`destination_uhid` = `peer_uhid`, TTL [`DEFAULT_TTL`]).
    /// Best-effort; returns delivery success. An empty `peer_uhid` is rejected
    /// (returns `false`).
    pub async fn publish_profile_to(&self, peer_uhid: &str) -> bool {
        if peer_uhid.is_empty() {
            return false;
        }

        let body = {
            let state = self.state.lock().unwrap();
            serde_json::to_vec(&state.local).unwrap_or_default()
        };

        let mut packet = MeshPacket::new(PacketType::ProfileSync, self.sender.local_uhid());
        packet.destination_uhid = peer_uhid.to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        self.sender.send(&packet, peer_uhid).await
    }

    /// Process an incoming [`PacketType::ProfileSync`] packet: cache the sender's
    /// profile (keyed by its `uhid`) and emit a [`ProfileUpdatedEvent`]. Ignores
    /// our own profile echoed back. Returns `false` for the wrong packet type, a
    /// malformed payload, an empty `uhid`, or our own profile; `true` once the
    /// profile has been cached.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::ProfileSync {
            return false;
        }

        let body: ProfileSyncPayload = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };
        if body.uhid.is_empty() {
            return false;
        }

        // Ignore our own profile echoed back.
        if body.uhid == self.sender.local_uhid() {
            return false;
        }

        {
            let mut state = self.state.lock().unwrap();
            state.peer_profiles.insert(body.uhid.clone(), body.clone());
        }

        // Best-effort: deliver to any subscribers. Ignore SendError when there
        // are no live receivers (fire-and-forget).
        let _ = self.profile_updated_tx.send(body);
        true
    }

    /// The cached profile for `uhid`, or `None` if none is known.
    pub fn get_profile(&self, uhid: &str) -> Option<ProfileSyncPayload> {
        let state = self.state.lock().unwrap();
        state.peer_profiles.get(uhid).cloned()
    }

    /// Snapshot of every peer profile this node has cached.
    pub fn get_known_profiles(&self) -> Vec<ProfileSyncPayload> {
        let state = self.state.lock().unwrap();
        state.peer_profiles.values().cloned().collect()
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

    // Byte-identity gate: `ProfileSyncPayload` must serialize to exactly these
    // bytes in every language (fixtures/profiles/vectors.json). snake_case keys,
    // field order uhid, display_name, avatar_ref, status_message, updated_at_ms,
    // no whitespace, updated_at_ms a bare integer, all string fields always
    // present. Mirrors the C# `ProfileSyncPayload_SerializesToCanonicalBytes`
    // (both InlineData vectors).
    #[test]
    fn profile_sync_payload_serializes_to_canonical_bytes() {
        let cases = [
            (
                "aether:alice:01",
                "Alice",
                "blake3:abc",
                "available",
                1_700_000_000_000i64,
                "{\"uhid\":\"aether:alice:01\",\"display_name\":\"Alice\",\"avatar_ref\":\"blake3:abc\",\"status_message\":\"available\",\"updated_at_ms\":1700000000000}",
            ),
            (
                "n",
                "",
                "",
                "",
                0i64,
                "{\"uhid\":\"n\",\"display_name\":\"\",\"avatar_ref\":\"\",\"status_message\":\"\",\"updated_at_ms\":0}",
            ),
        ];

        for (uhid, display_name, avatar_ref, status_message, updated_at_ms, expected) in cases {
            let payload = ProfileSyncPayload {
                uhid: uhid.to_string(),
                display_name: display_name.to_string(),
                avatar_ref: avatar_ref.to_string(),
                status_message: status_message.to_string(),
                updated_at_ms,
            };
            let bytes = serde_json::to_vec(&payload).unwrap();
            let json = String::from_utf8(bytes).unwrap();
            assert_eq!(
                json, expected,
                "byte-identity mismatch for uhid={uhid} updated_at_ms={updated_at_ms}"
            );
        }
    }
}
