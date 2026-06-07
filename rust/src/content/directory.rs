// SPDX-License-Identifier: MIT

//! Application-layer name → [`ContentDescriptor`] resolver. Mirrors the C#
//! `AetherNet.Content.DirectoryService` (commit 7f67f8d) and closes the
//! Wave-16 consumer-protocol-surface gap.
//!
//! [`crate::content::ContentDescriptor`] is content-addressed (`root_hash`-keyed)
//! — consumers that want to fetch content by an application-layer name (e.g.
//! `"podcast:abc123"`, `"reel:hash"`, `"album:artist/title"`) cannot do so via
//! the content surface alone because they do not know the `root_hash` upfront.
//! That's precisely what they're trying to discover.
//!
//! Wire flow:
//! ```text
//! publish:   broadcast NamePublish { name, descriptor, in_response_to_query_id=None }
//! resolve:   broadcast NameQuery   { name, query_id }
//!            ← unicast  NamePublish { name, descriptor, in_response_to_query_id=query_id }
//! ```
//!
//! Wire encoding: UTF-8 JSON with snake_case property names — byte-equal to
//! the C# reference. Wire-equality matters because cross-language fixtures
//! drive interop conformance.
//!
//! Added in v1.2.0 — closes C# Issue #60.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use tokio::sync::{broadcast, oneshot, Mutex, RwLock};
use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::content::ContentDescriptor;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

/// Default capacity for the `EntryAnnounced` broadcast channel — number of
/// in-flight announcements tolerated per subscriber before the slowest
/// subscriber starts losing events.
const ENTRY_ANNOUNCED_CHANNEL_CAPACITY: usize = 64;

/// Default timeout for [`DirectoryService::resolve`] when no value is supplied
/// by the caller. Matches the C# `DirectoryService.DefaultQueryTimeout`.
pub const DEFAULT_QUERY_TIMEOUT: Duration = Duration::from_secs(5);

// ─── Wire payloads ─────────────────────────────────────────────────────────

/// Wire payload for [`PacketType::NamePublish`].
///
/// Two modes:
/// * Unsolicited broadcast — publisher emits this on
///   [`DirectoryService::publish`]. `in_response_to_query_id` is `None`.
/// * Query response — a peer that holds the name emits this in unicast back
///   to a querier. `in_response_to_query_id` carries the query's correlation
///   id.
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub struct NamePublishPayload {
    /// The application-layer name being announced.
    pub name: String,

    /// The full descriptor that the name resolves to.
    pub descriptor: ContentDescriptor,

    /// If non-`None`, this is a unicast response to a prior
    /// [`PacketType::NameQuery`] whose `query_id` matched this value. If
    /// `None`, the publish is unsolicited.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub in_response_to_query_id: Option<Uuid>,
}

/// Wire payload for [`PacketType::NameQuery`]. A broadcast request asking
/// peers to send a [`NamePublishPayload`] for the named entry back to the
/// sender, correlated by `query_id`.
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub struct NameQueryPayload {
    /// The application-layer name being queried.
    pub name: String,

    /// Correlation id. Echoed by responders in
    /// [`NamePublishPayload::in_response_to_query_id`] so the querier can
    /// match responses to outstanding queries.
    pub query_id: Uuid,
}

// ─── Event payload ─────────────────────────────────────────────────────────

/// Event payload published on
/// [`DirectoryService::subscribe_entry_announced`] when a
/// [`PacketType::NamePublish`] packet arrives and the local catalogue learns a
/// new (or replaced) name → descriptor binding. Mirrors the C# event
/// `IDirectoryService.EntryAnnounced`.
#[derive(Clone, Debug)]
pub struct DirectoryEntryAnnouncedEvent {
    /// The newly-learned application-layer name.
    pub name: String,

    /// The descriptor the name resolves to.
    pub descriptor: ContentDescriptor,

    /// UHID of the peer that emitted the announcement.
    pub source_uhid: String,

    /// UTC time the announcement arrived locally.
    pub announced_at_utc: DateTime<Utc>,
}

// ─── Service ───────────────────────────────────────────────────────────────

/// Application-layer name → [`ContentDescriptor`] resolver. Holds a
/// [`MeshSender`] for outbound `NamePublish` / `NameQuery` packets, an
/// in-process catalogue, and a query-id → oneshot correlation map for
/// resolving outstanding queries when their responses arrive.
///
/// Persistence is the host's responsibility — rehydrate via
/// [`DirectoryService::publish`] on startup if you want a non-volatile
/// catalogue.
///
/// `Send + Sync` via the inner [`RwLock`] / [`Mutex`]; safe to share across
/// async tasks.
pub struct DirectoryService {
    sender: Arc<dyn MeshSender>,
    catalogue: RwLock<HashMap<String, ContentDescriptor>>,
    pending_queries: Mutex<HashMap<Uuid, oneshot::Sender<ContentDescriptor>>>,
    entry_announced_tx: broadcast::Sender<DirectoryEntryAnnouncedEvent>,
}

impl DirectoryService {
    /// Construct a new directory service backed by the given sender.
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (entry_announced_tx, _) =
            broadcast::channel(ENTRY_ANNOUNCED_CHANNEL_CAPACITY);
        Self {
            sender,
            catalogue: RwLock::new(HashMap::new()),
            pending_queries: Mutex::new(HashMap::new()),
            entry_announced_tx,
        }
    }

    /// Subscribe to `EntryAnnounced` events. Mirrors the C# event
    /// `IDirectoryService.EntryAnnounced`. Subscribers added AFTER an event
    /// was published will not see that event — `broadcast` is fire-and-forget.
    pub fn subscribe_entry_announced(
        &self,
    ) -> broadcast::Receiver<DirectoryEntryAnnouncedEvent> {
        self.entry_announced_tx.subscribe()
    }
}

/// Trait surface mirroring C# `IDirectoryService`. Hosts depending on the
/// abstraction (not the concrete `DirectoryService`) should hold an
/// `Arc<dyn DirectoryServiceApi>`.
#[async_trait]
pub trait DirectoryServiceApi: Send + Sync {
    /// Store the binding locally and broadcast a [`PacketType::NamePublish`]
    /// to every connected peer. Subsequent [`Self::resolve`] calls on the
    /// local node return the descriptor immediately from the catalogue.
    async fn publish(&self, name: &str, descriptor: ContentDescriptor);

    /// Resolve a name to its descriptor. Returns the local-catalogue hit
    /// immediately if present. Otherwise broadcasts a
    /// [`PacketType::NameQuery`] and awaits a matching
    /// [`PacketType::NamePublish`] response up to `query_timeout`. Returns
    /// `None` on timeout.
    async fn resolve(
        &self,
        name: &str,
        query_timeout: Option<Duration>,
    ) -> Option<ContentDescriptor>;

    /// Enumerate every name currently in the local catalogue (snapshot).
    async fn list_names(&self) -> Vec<String>;

    /// Pump inbound [`PacketType::NamePublish`] / [`PacketType::NameQuery`]
    /// packets into the service. Hosts wire this from their transport's
    /// receive pump.
    async fn handle(&self, packet: &MeshPacket);
}

#[async_trait]
impl DirectoryServiceApi for DirectoryService {
    async fn publish(&self, name: &str, descriptor: ContentDescriptor) {
        if name.is_empty() {
            return;
        }

        // Store first so a local resolve immediately after publish hits the
        // cache even if the broadcast is in-flight.
        {
            let mut cat = self.catalogue.write().await;
            cat.insert(name.to_string(), descriptor.clone());
        }

        let payload = NamePublishPayload {
            name: name.to_string(),
            descriptor,
            in_response_to_query_id: None,
        };
        let body = match serde_json::to_vec(&payload) {
            Ok(b) => b,
            Err(_) => return,
        };

        let mut packet = MeshPacket::new(PacketType::NamePublish, self.sender.local_uhid());
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;
        let _ = self.sender.broadcast(&packet).await;
    }

    async fn resolve(
        &self,
        name: &str,
        query_timeout: Option<Duration>,
    ) -> Option<ContentDescriptor> {
        if name.is_empty() {
            return None;
        }

        // Local-catalogue hit short-circuits — no broadcast.
        {
            let cat = self.catalogue.read().await;
            if let Some(d) = cat.get(name) {
                return Some(d.clone());
            }
        }

        let query_id = Uuid::new_v4();
        let (tx, rx) = oneshot::channel::<ContentDescriptor>();
        {
            let mut pending = self.pending_queries.lock().await;
            pending.insert(query_id, tx);
        }

        let payload = NameQueryPayload {
            name: name.to_string(),
            query_id,
        };
        let body = match serde_json::to_vec(&payload) {
            Ok(b) => b,
            Err(_) => {
                // Clean up the pending registration if serialisation failed.
                let mut pending = self.pending_queries.lock().await;
                pending.remove(&query_id);
                return None;
            }
        };

        let mut packet = MeshPacket::new(PacketType::NameQuery, self.sender.local_uhid());
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;
        let _ = self.sender.broadcast(&packet).await;

        let timeout = query_timeout.unwrap_or(DEFAULT_QUERY_TIMEOUT);
        let outcome = tokio::time::timeout(timeout, rx).await;

        // Always clean up the pending entry — either the response arrived
        // (and the sender was already dropped by the handler), the timeout
        // fired, or the sender side was dropped.
        {
            let mut pending = self.pending_queries.lock().await;
            pending.remove(&query_id);
        }

        match outcome {
            Ok(Ok(d)) => Some(d),
            _ => None,
        }
    }

    async fn list_names(&self) -> Vec<String> {
        let cat = self.catalogue.read().await;
        cat.keys().cloned().collect()
    }

    async fn handle(&self, packet: &MeshPacket) {
        match packet.packet_type {
            PacketType::NamePublish => self.handle_publish(packet).await,
            PacketType::NameQuery => self.handle_query(packet).await,
            _ => {}
        }
    }
}

impl DirectoryService {
    async fn handle_publish(&self, packet: &MeshPacket) {
        let payload: NamePublishPayload = match serde_json::from_slice(&packet.payload) {
            Ok(p) => p,
            Err(_) => return,
        };
        if payload.name.is_empty() {
            return;
        }

        // Store in catalogue first so an EntryAnnounced subscriber can resolve
        // the binding synchronously from inside its event handler.
        {
            let mut cat = self.catalogue.write().await;
            cat.insert(payload.name.clone(), payload.descriptor.clone());
        }

        // Query-response correlation — if this NamePublish carries the
        // in_response_to_query_id from one of our outstanding resolves, hand
        // the descriptor to the awaiter via its oneshot sender.
        if let Some(query_id) = payload.in_response_to_query_id {
            let mut pending = self.pending_queries.lock().await;
            if let Some(tx) = pending.remove(&query_id) {
                let _ = tx.send(payload.descriptor.clone());
            }
        }

        let evt = DirectoryEntryAnnouncedEvent {
            name: payload.name,
            descriptor: payload.descriptor,
            source_uhid: packet.source_uhid.clone(),
            announced_at_utc: Utc::now(),
        };
        let _ = self.entry_announced_tx.send(evt);
    }

    async fn handle_query(&self, packet: &MeshPacket) {
        let payload: NameQueryPayload = match serde_json::from_slice(&packet.payload) {
            Ok(p) => p,
            Err(_) => return,
        };
        if payload.name.is_empty() {
            return;
        }

        // Only respond if we hold the name locally. Silent ignore otherwise —
        // other peers may answer. Mirrors C# handler.
        let descriptor = {
            let cat = self.catalogue.read().await;
            match cat.get(&payload.name) {
                Some(d) => d.clone(),
                None => return,
            }
        };

        let response = NamePublishPayload {
            name: payload.name,
            descriptor,
            in_response_to_query_id: Some(payload.query_id),
        };
        let body = match serde_json::to_vec(&response) {
            Ok(b) => b,
            Err(_) => return,
        };

        let mut reply = MeshPacket::new(PacketType::NamePublish, self.sender.local_uhid());
        reply.destination_uhid = packet.source_uhid.clone();
        reply.ttl = DEFAULT_TTL;
        reply.payload = body;
        let _ = self.sender.send(&reply, &packet.source_uhid).await;
    }
}

// ─── Tests ─────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use std::sync::Mutex as StdMutex;

    use crate::models::PeerInfo;

    /// Recording mesh sender for directory tests.
    struct CapturingSender {
        local: String,
        inner: StdMutex<Inner>,
    }

    struct Inner {
        peers: Vec<PeerInfo>,
        unicasts: Vec<(MeshPacket, String)>,
        broadcasts: Vec<MeshPacket>,
    }

    impl CapturingSender {
        fn new(local: &str) -> Arc<Self> {
            Arc::new(Self {
                local: local.to_string(),
                inner: StdMutex::new(Inner {
                    peers: Vec::new(),
                    unicasts: Vec::new(),
                    broadcasts: Vec::new(),
                }),
            })
        }

        fn add_peer(&self, uhid: &str) {
            self.inner.lock().unwrap().peers.push(PeerInfo {
                uhid: uhid.to_string(),
                public_key: vec![],
                last_seen: std::time::SystemTime::now(),
                hop_count: 0,
                reliability_score: 50,
                capabilities: 0,
                geohash: None,
                is_blocked: false,
            });
        }

        fn broadcasts(&self) -> Vec<MeshPacket> {
            self.inner.lock().unwrap().broadcasts.clone()
        }

        fn unicasts(&self) -> Vec<(MeshPacket, String)> {
            self.inner.lock().unwrap().unicasts.clone()
        }

        fn clear(&self) {
            let mut g = self.inner.lock().unwrap();
            g.broadcasts.clear();
            g.unicasts.clear();
        }
    }

    #[async_trait]
    impl MeshSender for CapturingSender {
        fn local_uhid(&self) -> String {
            self.local.clone()
        }
        fn connected_peers(&self) -> Vec<PeerInfo> {
            self.inner.lock().unwrap().peers.clone()
        }
        async fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool {
            self.inner
                .lock()
                .unwrap()
                .unicasts
                .push((packet.clone(), next_hop_uhid.to_string()));
            true
        }
        async fn broadcast(&self, packet: &MeshPacket) -> usize {
            let mut g = self.inner.lock().unwrap();
            g.broadcasts.push(packet.clone());
            g.peers.len()
        }
    }

    fn sample_descriptor(root_hash: &str) -> ContentDescriptor {
        ContentDescriptor {
            root_hash: root_hash.to_string(),
            name: "ignored-publisher-hint".to_string(),
            total_bytes: 1024,
            chunk_size_bytes: 256,
            chunk_count: 4,
            chunk_hashes: vec![
                "h0".to_string(),
                "h1".to_string(),
                "h2".to_string(),
                "h3".to_string(),
            ],
            content_type: "audio/flac".to_string(),
            created_at: None,
        }
    }

    // ─── publish ────────────────────────────────────────────────────

    #[tokio::test]
    async fn publish_stores_locally_and_broadcasts_name_publish() {
        let sender = CapturingSender::new("publisher");
        sender.add_peer("peer-1");
        sender.add_peer("peer-2");
        let dir = DirectoryService::new(sender.clone() as Arc<dyn MeshSender>);

        dir.publish("podcast:abc", sample_descriptor("root-abc"))
            .await;

        // Local resolve hits the catalogue immediately.
        let hit = dir.resolve("podcast:abc", None).await;
        assert!(hit.is_some());
        assert_eq!(hit.unwrap().root_hash, "root-abc");

        // Broadcast went out.
        let bcasts = sender.broadcasts();
        assert_eq!(bcasts.len(), 1);
        assert_eq!(bcasts[0].packet_type, PacketType::NamePublish);

        // Wire payload must be snake_case JSON.
        let body: serde_json::Value =
            serde_json::from_slice(&bcasts[0].payload).expect("valid json");
        assert!(body.get("name").is_some());
        assert!(body.get("descriptor").is_some());
        assert!(body.get("in_response_to_query_id").is_none() || body["in_response_to_query_id"].is_null());
        // Descriptor inner fields must also be snake_case.
        let desc = &body["descriptor"];
        assert!(desc.get("root_hash").is_some(), "snake_case required");
        assert!(desc.get("rootHash").is_none(), "must NOT be camelCase");
    }

    #[tokio::test]
    async fn resolve_local_hit_returns_immediately_no_broadcast() {
        let sender = CapturingSender::new("local");
        sender.add_peer("peer-1");
        let dir = DirectoryService::new(sender.clone() as Arc<dyn MeshSender>);

        dir.publish("track:xyz", sample_descriptor("root-xyz"))
            .await;
        sender.clear();

        let hit = dir.resolve("track:xyz", None).await;

        assert!(hit.is_some());
        assert_eq!(hit.unwrap().root_hash, "root-xyz");
        // No NameQuery sent — local hit.
        assert!(sender.broadcasts().is_empty());
    }

    // ─── handle(NamePublish) ────────────────────────────────────────

    #[tokio::test]
    async fn handle_inbound_name_publish_populates_catalogue_and_fires_event() {
        let sender = CapturingSender::new("local");
        let dir = DirectoryService::new(sender.clone() as Arc<dyn MeshSender>);
        let mut rx = dir.subscribe_entry_announced();

        // Build a NamePublish packet from a peer.
        let payload = NamePublishPayload {
            name: "reel:hello".to_string(),
            descriptor: sample_descriptor("from-peer"),
            in_response_to_query_id: None,
        };
        let mut packet = MeshPacket::new(
            PacketType::NamePublish,
            "peer-publisher".to_string(),
        );
        packet.payload = serde_json::to_vec(&payload).unwrap();
        dir.handle(&packet).await;

        // Catalogue now has the entry.
        let hit = dir.resolve("reel:hello", None).await;
        assert!(hit.is_some());
        assert_eq!(hit.unwrap().root_hash, "from-peer");

        // Event fired.
        let evt = tokio::time::timeout(Duration::from_millis(200), rx.recv())
            .await
            .expect("EntryAnnounced must fire within 200ms")
            .expect("recv yields Ok");
        assert_eq!(evt.name, "reel:hello");
        assert_eq!(evt.source_uhid, "peer-publisher");
        assert_eq!(evt.descriptor.root_hash, "from-peer");
    }

    // ─── handle(NameQuery) ──────────────────────────────────────────

    #[tokio::test]
    async fn handle_query_with_matching_name_unicasts_name_publish_response() {
        let holder_sender = CapturingSender::new("holder");
        holder_sender.add_peer("asker");
        let holder = DirectoryService::new(holder_sender.clone() as Arc<dyn MeshSender>);

        holder
            .publish("album:test", sample_descriptor("album-root"))
            .await;
        holder_sender.clear();

        let query_id = Uuid::new_v4();
        let query_payload = NameQueryPayload {
            name: "album:test".to_string(),
            query_id,
        };
        let mut query_packet =
            MeshPacket::new(PacketType::NameQuery, "asker".to_string());
        query_packet.payload = serde_json::to_vec(&query_payload).unwrap();

        holder.handle(&query_packet).await;

        let unicasts = holder_sender.unicasts();
        assert_eq!(unicasts.len(), 1);
        let (response_packet, next_hop) = &unicasts[0];
        assert_eq!(next_hop, "asker");
        assert_eq!(response_packet.packet_type, PacketType::NamePublish);

        let response: NamePublishPayload =
            serde_json::from_slice(&response_packet.payload).unwrap();
        assert_eq!(response.name, "album:test");
        assert_eq!(response.descriptor.root_hash, "album-root");
        assert_eq!(response.in_response_to_query_id, Some(query_id));
    }

    #[tokio::test]
    async fn handle_query_for_unknown_name_does_nothing() {
        let sender = CapturingSender::new("local");
        sender.add_peer("asker");
        let dir = DirectoryService::new(sender.clone() as Arc<dyn MeshSender>);

        let query_payload = NameQueryPayload {
            name: "nothing-here".to_string(),
            query_id: Uuid::new_v4(),
        };
        let mut packet = MeshPacket::new(PacketType::NameQuery, "asker".to_string());
        packet.payload = serde_json::to_vec(&query_payload).unwrap();

        dir.handle(&packet).await;

        assert!(sender.unicasts().is_empty());
        assert!(sender.broadcasts().is_empty());
    }

    // ─── resolve timeout / response ─────────────────────────────────

    #[tokio::test]
    async fn resolve_miss_and_timeout_returns_none() {
        let sender = CapturingSender::new("local");
        sender.add_peer("peer-1");
        let dir = DirectoryService::new(sender.clone() as Arc<dyn MeshSender>);

        let hit = dir
            .resolve("unknown-name", Some(Duration::from_millis(50)))
            .await;

        assert!(hit.is_none());
        // A NameQuery WAS broadcast — we tried.
        let bcasts = sender.broadcasts();
        assert_eq!(bcasts.len(), 1);
        assert_eq!(bcasts[0].packet_type, PacketType::NameQuery);
    }

    #[tokio::test]
    async fn resolve_query_and_answer_arrives_returns_descriptor() {
        let sender = CapturingSender::new("local");
        sender.add_peer("peer-1");
        let dir = Arc::new(DirectoryService::new(
            sender.clone() as Arc<dyn MeshSender>
        ));

        // Start a resolve in the background.
        let dir_clone = dir.clone();
        let resolve_task = tokio::spawn(async move {
            dir_clone
                .resolve("podcast:remote", Some(Duration::from_secs(2)))
                .await
        });

        // Wait briefly for the NameQuery to be broadcast.
        tokio::time::sleep(Duration::from_millis(50)).await;

        let bcasts = sender.broadcasts();
        assert_eq!(bcasts.len(), 1);
        let query_broadcast = &bcasts[0];
        assert_eq!(query_broadcast.packet_type, PacketType::NameQuery);

        let query: NameQueryPayload =
            serde_json::from_slice(&query_broadcast.payload).unwrap();

        // Simulate a peer responding with a NamePublish carrying
        // in_response_to_query_id.
        let descriptor = sample_descriptor("remote-root");
        let response_payload = NamePublishPayload {
            name: "podcast:remote".to_string(),
            descriptor,
            in_response_to_query_id: Some(query.query_id),
        };
        let mut response_packet = MeshPacket::new(
            PacketType::NamePublish,
            "peer-1".to_string(),
        );
        response_packet.payload = serde_json::to_vec(&response_payload).unwrap();
        dir.handle(&response_packet).await;

        let result = resolve_task.await.expect("task joined");
        assert!(result.is_some());
        assert_eq!(result.unwrap().root_hash, "remote-root");
    }

    // ─── list_names ────────────────────────────────────────────────

    #[tokio::test]
    async fn list_names_returns_catalogue_snapshot() {
        let sender = CapturingSender::new("local");
        let dir = DirectoryService::new(sender.clone() as Arc<dyn MeshSender>);

        dir.publish("a", sample_descriptor("hash-a")).await;
        dir.publish("b", sample_descriptor("hash-b")).await;
        dir.publish("c", sample_descriptor("hash-c")).await;

        let mut names = dir.list_names().await;
        names.sort();
        assert_eq!(names, vec!["a".to_string(), "b".to_string(), "c".to_string()]);
    }
}
