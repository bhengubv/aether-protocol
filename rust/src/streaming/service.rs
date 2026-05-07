// SPDX-License-Identifier: MIT

//! Live streaming service: announce streams, subscribe, publish segments.

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;
use crate::routing::RoutingService;

// ── Wire types ─────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct StreamAnnouncePayload {
    pub stream_id: Uuid,
    pub title: String,
    pub content_type: String,
    pub codec: String,
    pub segment_duration_ms: u64,
    pub state: String,
    pub started_at_ms: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct StreamSubscribePayload {
    pub stream_id: Uuid,
    pub live_only: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct StreamUnsubscribePayload {
    pub stream_id: Uuid,
}

// ── Stream state ───────────────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct StreamEntry {
    pub stream_id: Uuid,
    pub title: String,
    pub content_type: String,
    pub codec: String,
    pub segment_duration_ms: u64,
    pub started_at_ms: i64,
    pub active: bool,
}

// ── Service ───────────────────────────────────────────────────────────────────

pub struct StreamingService {
    sender: Arc<dyn MeshSender>,
    routing: Arc<RoutingService>,
    /// Streams this node is publishing.
    streams: Mutex<HashMap<Uuid, StreamEntry>>,
    /// stream_id -> set of subscriber UHIDs.
    subscribers: Mutex<HashMap<Uuid, HashSet<String>>>,
}

impl StreamingService {
    pub fn new(sender: Arc<dyn MeshSender>, routing: Arc<RoutingService>) -> Self {
        Self {
            sender,
            routing,
            streams: Mutex::new(HashMap::new()),
            subscribers: Mutex::new(HashMap::new()),
        }
    }

    /// Start a new stream. Returns the stream_id.
    pub async fn start_stream(
        &self,
        title: &str,
        content_type: &str,
        codec: &str,
        segment_duration_ms: u64,
    ) -> Result<Uuid, String> {
        let stream_id = Uuid::new_v4();
        let now = unix_millis();
        let entry = StreamEntry {
            stream_id,
            title: title.to_string(),
            content_type: content_type.to_string(),
            codec: codec.to_string(),
            segment_duration_ms,
            started_at_ms: now,
            active: true,
        };
        {
            let mut streams = self.streams.lock().unwrap();
            streams.insert(stream_id, entry.clone());
        }
        let ann = StreamAnnouncePayload {
            stream_id,
            title: entry.title.clone(),
            content_type: entry.content_type.clone(),
            codec: entry.codec.clone(),
            segment_duration_ms,
            state: "live".into(),
            started_at_ms: now,
        };
        self.broadcast_announce(&ann).await;
        Ok(stream_id)
    }

    /// End a stream and notify subscribers.
    pub async fn end_stream(&self, stream_id: Uuid) -> Result<(), String> {
        let entry = {
            let mut streams = self.streams.lock().unwrap();
            let entry = streams
                .get_mut(&stream_id)
                .ok_or_else(|| format!("unknown stream_id {}", stream_id))?;
            entry.active = false;
            entry.clone()
        };
        let ann = StreamAnnouncePayload {
            stream_id,
            title: entry.title,
            content_type: entry.content_type,
            codec: entry.codec,
            segment_duration_ms: entry.segment_duration_ms,
            state: "ended".into(),
            started_at_ms: entry.started_at_ms,
        };
        self.broadcast_announce(&ann).await;
        Ok(())
    }

    /// Subscribe to a stream published by `publisher_uhid`.
    pub async fn subscribe(
        &self,
        stream_id: Uuid,
        publisher_uhid: &str,
        live_only: bool,
    ) -> Result<(), String> {
        if publisher_uhid.is_empty() {
            return Err("publisher_uhid is empty".into());
        }
        {
            let mut subs = self.subscribers.lock().unwrap();
            subs.entry(stream_id)
                .or_insert_with(HashSet::new)
                .insert(self.sender.local_uhid());
        }
        let body = serde_json::to_vec(&StreamSubscribePayload { stream_id, live_only })
            .unwrap_or_default();
        let route = self.routing.find_route(publisher_uhid).await;
        let next_hop = route
            .map(|r| r.next_hop_uhid)
            .unwrap_or_else(|| publisher_uhid.to_string());

        let mut pkt = MeshPacket::new(PacketType::StreamSubscribe, self.sender.local_uhid());
        pkt.destination_uhid = publisher_uhid.to_string();
        pkt.ttl = DEFAULT_TTL;
        pkt.payload = body;
        self.sender.send(&pkt, &next_hop).await;
        Ok(())
    }

    /// Unsubscribe from a stream.
    pub async fn unsubscribe(
        &self,
        stream_id: Uuid,
        publisher_uhid: &str,
    ) -> Result<(), String> {
        {
            let mut subs = self.subscribers.lock().unwrap();
            if let Some(set) = subs.get_mut(&stream_id) {
                set.remove(&self.sender.local_uhid());
            }
        }
        let body = serde_json::to_vec(&StreamUnsubscribePayload { stream_id })
            .unwrap_or_default();
        let route = self.routing.find_route(publisher_uhid).await;
        let next_hop = route
            .map(|r| r.next_hop_uhid)
            .unwrap_or_else(|| publisher_uhid.to_string());

        let mut pkt = MeshPacket::new(PacketType::StreamUnsubscribe, self.sender.local_uhid());
        pkt.destination_uhid = publisher_uhid.to_string();
        pkt.ttl = DEFAULT_TTL;
        pkt.payload = body;
        self.sender.send(&pkt, &next_hop).await;
        Ok(())
    }

    /// Publish a segment to all current subscribers.
    ///
    /// Binary payload wire format:
    ///   [16] StreamId (UUID RFC4122 big-endian)
    ///   [4]  Sequence (u32 little-endian)
    ///   [8]  TimestampMs (i64 little-endian)
    ///   [1]  IsKeyframe (0 or 1)
    ///   [N]  EncodedPayload
    pub async fn publish_segment(
        &self,
        stream_id: Uuid,
        data: &[u8],
        is_keyframe: bool,
    ) -> Result<(), String> {
        {
            let streams = self.streams.lock().unwrap();
            if !streams
                .get(&stream_id)
                .map(|s| s.active)
                .unwrap_or(false)
            {
                return Err(format!("stream {} is not active", stream_id));
            }
        }

        let subs: Vec<String> = {
            let subs = self.subscribers.lock().unwrap();
            subs.get(&stream_id)
                .cloned()
                .unwrap_or_default()
                .into_iter()
                .collect()
        };

        let payload = build_stream_segment(stream_id, data, is_keyframe);
        let local = self.sender.local_uhid();

        for uhid in &subs {
            let mut pkt = MeshPacket::new(PacketType::StreamSegment, local.clone());
            pkt.destination_uhid = uhid.clone();
            pkt.ttl = DEFAULT_TTL;
            pkt.priority = 64;
            pkt.payload = payload.clone();
            self.sender.send(&pkt, uhid).await;
        }
        Ok(())
    }

    /// Route an inbound packet to the right handler.
    pub async fn handle_packet(&self, packet: &MeshPacket) -> Result<(), String> {
        match packet.packet_type {
            PacketType::StreamAnnounce => self.handle_announce(packet).await,
            PacketType::StreamSubscribe => self.handle_subscribe(packet).await,
            PacketType::StreamUnsubscribe => self.handle_unsubscribe(packet).await,
            PacketType::StreamSegment => Ok(()), // surfaced to app layer
            _ => Ok(()),
        }
    }

    // ── private ────────────────────────────────────────────────────────────────

    async fn broadcast_announce(&self, ann: &StreamAnnouncePayload) {
        let body = match serde_json::to_vec(ann) {
            Ok(b) => b,
            Err(_) => return,
        };
        let mut pkt = MeshPacket::new(PacketType::StreamAnnounce, self.sender.local_uhid());
        pkt.destination_uhid = String::new();
        pkt.ttl = DEFAULT_TTL;
        pkt.payload = body;
        self.sender.broadcast(&pkt).await;
    }

    async fn handle_announce(&self, _packet: &MeshPacket) -> Result<(), String> {
        // Announcements from remote publishers are surfaced to the app layer.
        Ok(())
    }

    async fn handle_subscribe(&self, packet: &MeshPacket) -> Result<(), String> {
        let sub: StreamSubscribePayload = serde_json::from_slice(&packet.payload)
            .map_err(|e| format!("bad subscribe payload: {}", e))?;
        let mut subs = self.subscribers.lock().unwrap();
        subs.entry(sub.stream_id)
            .or_insert_with(HashSet::new)
            .insert(packet.source_uhid.clone());
        Ok(())
    }

    async fn handle_unsubscribe(&self, packet: &MeshPacket) -> Result<(), String> {
        let unsub: StreamUnsubscribePayload = serde_json::from_slice(&packet.payload)
            .map_err(|e| format!("bad unsubscribe payload: {}", e))?;
        let mut subs = self.subscribers.lock().unwrap();
        if let Some(set) = subs.get_mut(&unsub.stream_id) {
            set.remove(&packet.source_uhid);
        }
        Ok(())
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static SEG_SEQ: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

fn build_stream_segment(stream_id: Uuid, data: &[u8], is_keyframe: bool) -> Vec<u8> {
    let seq = SEG_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let ts = unix_millis();
    let mut buf = Vec::with_capacity(29 + data.len());
    buf.extend_from_slice(stream_id.as_bytes()); // [16]
    buf.extend_from_slice(&seq.to_le_bytes()); // [4]
    buf.extend_from_slice(&ts.to_le_bytes()); // [8]
    buf.push(if is_keyframe { 1 } else { 0 }); // [1]
    buf.extend_from_slice(data); // [N]
    buf
}

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}
