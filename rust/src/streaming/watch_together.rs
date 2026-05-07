// SPDX-License-Identifier: MIT

//! Watch-Together service: synchronized playback across session members.

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
pub struct WatchSyncPayload {
    pub session_id: Uuid,
    pub kind: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub position_ms: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub playback_speed: Option<f64>,
    pub sent_at_ms: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub content_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct WatchReactionPayload {
    pub session_id: Uuid,
    pub reaction: String,
}

// ── Session state ──────────────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct WatchSession {
    pub session_id: Uuid,
    pub content_id: Option<String>,
    pub members: HashSet<String>,
    /// Current playback position in ms (at time of last sync).
    pub position_ms: i64,
    /// When the last sync was recorded (unix ms).
    pub last_sync_at_ms: i64,
    /// Current playback speed (1.0 = normal).
    pub playback_speed: f64,
    pub playing: bool,
}

// ── Service ───────────────────────────────────────────────────────────────────

pub struct WatchTogetherService {
    sender: Arc<dyn MeshSender>,
    routing: Arc<RoutingService>,
    sessions: Mutex<HashMap<Uuid, WatchSession>>,
}

impl WatchTogetherService {
    pub fn new(sender: Arc<dyn MeshSender>, routing: Arc<RoutingService>) -> Self {
        Self {
            sender,
            routing,
            sessions: Mutex::new(HashMap::new()),
        }
    }

    /// Invite members to a new or existing watch session. The content_id
    /// is broadcast to all invitees.
    pub async fn invite_to_session(
        &self,
        session_id: Uuid,
        content_id: &str,
        member_uhids: &[String],
    ) -> Result<(), String> {
        if member_uhids.is_empty() {
            return Err("member_uhids is empty".into());
        }
        let now = unix_millis();
        {
            let mut sessions = self.sessions.lock().unwrap();
            let entry = sessions.entry(session_id).or_insert_with(|| WatchSession {
                session_id,
                content_id: Some(content_id.to_string()),
                members: HashSet::new(),
                position_ms: 0,
                last_sync_at_ms: now,
                playback_speed: 1.0,
                playing: false,
            });
            for m in member_uhids {
                entry.members.insert(m.clone());
            }
        }
        for uhid in member_uhids {
            let sync = WatchSyncPayload {
                session_id,
                kind: "join".into(),
                position_ms: Some(0),
                playback_speed: Some(1.0),
                sent_at_ms: now,
                content_id: Some(content_id.to_string()),
            };
            self.send_sync(uhid, &sync).await;
        }
        Ok(())
    }

    /// Broadcast a play command to all session members.
    pub async fn play(&self, session_id: Uuid, position_ms: i64) -> Result<(), String> {
        let now = unix_millis();
        let (speed, members) = self.update_session(session_id, |s| {
            s.playing = true;
            s.position_ms = position_ms;
            s.last_sync_at_ms = now;
            (s.playback_speed, s.members.clone())
        })?;
        let sync = WatchSyncPayload {
            session_id,
            kind: "play".into(),
            position_ms: Some(position_ms),
            playback_speed: Some(speed),
            sent_at_ms: now,
            content_id: None,
        };
        self.broadcast_sync(session_id, &members, &sync).await;
        Ok(())
    }

    /// Broadcast a pause command.
    pub async fn pause(&self, session_id: Uuid, position_ms: i64) -> Result<(), String> {
        let now = unix_millis();
        let (_, members) = self.update_session(session_id, |s| {
            s.playing = false;
            s.position_ms = position_ms;
            s.last_sync_at_ms = now;
            (s.playback_speed, s.members.clone())
        })?;
        let sync = WatchSyncPayload {
            session_id,
            kind: "pause".into(),
            position_ms: Some(position_ms),
            playback_speed: None,
            sent_at_ms: now,
            content_id: None,
        };
        self.broadcast_sync(session_id, &members, &sync).await;
        Ok(())
    }

    /// Broadcast a seek command.
    pub async fn seek(&self, session_id: Uuid, position_ms: i64) -> Result<(), String> {
        let now = unix_millis();
        let (_, members) = self.update_session(session_id, |s| {
            s.position_ms = position_ms;
            s.last_sync_at_ms = now;
            (s.playback_speed, s.members.clone())
        })?;
        let sync = WatchSyncPayload {
            session_id,
            kind: "seek".into(),
            position_ms: Some(position_ms),
            playback_speed: None,
            sent_at_ms: now,
            content_id: None,
        };
        self.broadcast_sync(session_id, &members, &sync).await;
        Ok(())
    }

    /// Broadcast a playback speed change.
    pub async fn set_speed(&self, session_id: Uuid, playback_speed: f64) -> Result<(), String> {
        let now = unix_millis();
        let (pos, members) = self.update_session(session_id, |s| {
            s.playback_speed = playback_speed;
            s.last_sync_at_ms = now;
            (s.position_ms, s.members.clone())
        })?;
        let sync = WatchSyncPayload {
            session_id,
            kind: "speed".into(),
            position_ms: Some(pos),
            playback_speed: Some(playback_speed),
            sent_at_ms: now,
            content_id: None,
        };
        self.broadcast_sync(session_id, &members, &sync).await;
        Ok(())
    }

    /// Send an emoji/text reaction to all session members.
    pub async fn send_reaction(
        &self,
        session_id: Uuid,
        reaction: &str,
    ) -> Result<(), String> {
        let members = {
            let sessions = self.sessions.lock().unwrap();
            sessions
                .get(&session_id)
                .ok_or_else(|| format!("unknown session_id {}", session_id))?
                .members
                .clone()
        };
        let body = serde_json::to_vec(&WatchReactionPayload {
            session_id,
            reaction: reaction.to_string(),
        })
        .unwrap_or_default();
        let local = self.sender.local_uhid();
        for uhid in &members {
            if *uhid == local {
                continue;
            }
            let mut pkt = MeshPacket::new(PacketType::WatchReaction, local.clone());
            pkt.destination_uhid = uhid.clone();
            pkt.ttl = DEFAULT_TTL;
            pkt.payload = body.clone();
            self.sender.send(&pkt, uhid).await;
        }
        Ok(())
    }

    /// Route an inbound packet to the right handler.
    pub async fn handle_packet(&self, packet: &MeshPacket) -> Result<(), String> {
        match packet.packet_type {
            PacketType::WatchSync => self.handle_sync(packet).await,
            PacketType::WatchReaction => Ok(()), // surfaced to app layer
            _ => Ok(()),
        }
    }

    // ── private ────────────────────────────────────────────────────────────────

    /// Send a WatchSync to a single peer.
    async fn send_sync(&self, to_uhid: &str, sync: &WatchSyncPayload) {
        let body = match serde_json::to_vec(sync) {
            Ok(b) => b,
            Err(_) => return,
        };
        let route = self.routing.find_route(to_uhid).await;
        let next_hop = route
            .map(|r| r.next_hop_uhid)
            .unwrap_or_else(|| to_uhid.to_string());

        let mut pkt = MeshPacket::new(PacketType::WatchSync, self.sender.local_uhid());
        pkt.destination_uhid = to_uhid.to_string();
        pkt.ttl = DEFAULT_TTL;
        pkt.payload = body;
        self.sender.send(&pkt, &next_hop).await;
    }

    /// Broadcast a WatchSync to all members except self.
    async fn broadcast_sync(
        &self,
        _session_id: Uuid,
        members: &HashSet<String>,
        sync: &WatchSyncPayload,
    ) {
        let local = self.sender.local_uhid();
        for uhid in members {
            if *uhid == local {
                continue;
            }
            self.send_sync(uhid, sync).await;
        }
    }

    async fn handle_sync(&self, packet: &MeshPacket) -> Result<(), String> {
        let sync: WatchSyncPayload = serde_json::from_slice(&packet.payload)
            .map_err(|e| format!("bad watch sync payload: {}", e))?;

        let now = unix_millis();
        // RTT compensation: advance position by elapsed time * speed.
        let elapsed_ms = now - sync.sent_at_ms;
        let speed = sync.playback_speed.unwrap_or(1.0);
        let compensated_position = sync.position_ms.map(|pos| {
            pos + ((elapsed_ms as f64) * speed) as i64
        });

        let mut sessions = self.sessions.lock().unwrap();
        let entry = sessions.entry(sync.session_id).or_insert_with(|| WatchSession {
            session_id: sync.session_id,
            content_id: sync.content_id.clone(),
            members: HashSet::new(),
            position_ms: compensated_position.unwrap_or(0),
            last_sync_at_ms: now,
            playback_speed: speed,
            playing: false,
        });

        match sync.kind.as_str() {
            "join" => {
                entry.members.insert(packet.source_uhid.clone());
                if let Some(cid) = sync.content_id {
                    entry.content_id = Some(cid);
                }
            }
            "leave" | "end" => {
                entry.members.remove(&packet.source_uhid);
            }
            "play" => {
                entry.playing = true;
                entry.playback_speed = speed;
                if let Some(pos) = compensated_position {
                    entry.position_ms = pos;
                }
                entry.last_sync_at_ms = now;
            }
            "pause" => {
                entry.playing = false;
                if let Some(pos) = sync.position_ms {
                    entry.position_ms = pos;
                }
                entry.last_sync_at_ms = now;
            }
            "seek" => {
                if let Some(pos) = compensated_position {
                    entry.position_ms = pos;
                }
                entry.last_sync_at_ms = now;
            }
            "speed" => {
                entry.playback_speed = speed;
                entry.last_sync_at_ms = now;
            }
            _ => {}
        }
        Ok(())
    }

    /// Helper: mutate a session under lock, returning a value.
    fn update_session<T, F>(&self, session_id: Uuid, f: F) -> Result<T, String>
    where
        F: FnOnce(&mut WatchSession) -> T,
    {
        let mut sessions = self.sessions.lock().unwrap();
        let entry = sessions
            .get_mut(&session_id)
            .ok_or_else(|| format!("unknown session_id {}", session_id))?;
        Ok(f(entry))
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}
