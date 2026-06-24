// SPDX-License-Identifier: MIT

//! Group voice call service: multi-party signaling + binary frame delivery.

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;
use tokio::sync::broadcast;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;
use crate::routing::RoutingService;

// ── Signaling wire type ────────────────────────────────────────────────────────

/// JSON signaling for group voice. Cross-language canonical wire format.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct GroupVoiceSignalingMessage {
    pub kind: String,
    pub call_id: Uuid,
    pub from_uhid: String,
    pub to_uhid: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub invited_uhids: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub kicked_uhid: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub key_generation: Option<u32>,
}

// ── Group call state ───────────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct GroupCallEntry {
    pub call_id: Uuid,
    pub host_uhid: String,
    pub members: HashSet<String>,
    pub active: bool,
}

// ── Inbound frame event ────────────────────────────────────────────────────────

/// Capacity of the inbound-frame broadcast channel; lagging subscribers drop oldest.
const FRAME_RECEIVED_CHANNEL_CAPACITY: usize = 256;

/// An inbound group-voice frame decoded from a received VOICE_CALL packet, delivered
/// to subscribers of [`GroupVoiceCallService::subscribe_frames`].
#[derive(Debug, Clone)]
pub struct GroupVoiceFrameReceivedEvent {
    pub call_id: Uuid,
    pub sender_uhid: String,
    pub seq: u32,
    pub timestamp_ms: i64,
    pub is_silence: bool,
    pub key_generation: u32,
    pub audio: Vec<u8>,
}

// ── Service ───────────────────────────────────────────────────────────────────

pub struct GroupVoiceCallService {
    sender: Arc<dyn MeshSender>,
    routing: Arc<RoutingService>,
    calls: Mutex<HashMap<Uuid, GroupCallEntry>>,
    frame_received_tx: broadcast::Sender<GroupVoiceFrameReceivedEvent>,
}

impl GroupVoiceCallService {
    pub fn new(sender: Arc<dyn MeshSender>, routing: Arc<RoutingService>) -> Self {
        let (frame_received_tx, _) = broadcast::channel(FRAME_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            routing,
            calls: Mutex::new(HashMap::new()),
            frame_received_tx,
        }
    }

    /// Subscribe to inbound group-voice frames decoded from received VOICE_CALL packets.
    pub fn subscribe_frames(&self) -> broadcast::Receiver<GroupVoiceFrameReceivedEvent> {
        self.frame_received_tx.subscribe()
    }

    /// Invite a set of members to a (new or existing) group call.
    pub async fn invite(
        &self,
        call_id: Uuid,
        member_uhids: &[String],
    ) -> Result<(), String> {
        if member_uhids.is_empty() {
            return Err("member_uhids is empty".into());
        }
        let local = self.sender.local_uhid();
        {
            let mut calls = self.calls.lock().unwrap();
            let entry = calls.entry(call_id).or_insert_with(|| GroupCallEntry {
                call_id,
                host_uhid: local.clone(),
                members: HashSet::new(),
                active: true,
            });
            for m in member_uhids {
                entry.members.insert(m.clone());
            }
        }
        for uhid in member_uhids {
            let sig = GroupVoiceSignalingMessage {
                kind: "invite".into(),
                call_id,
                from_uhid: local.clone(),
                to_uhid: uhid.clone(),
                invited_uhids: Some(member_uhids.to_vec()),
                kicked_uhid: None,
                key_generation: None,
            };
            self.send_signaling(uhid, &sig).await;
        }
        Ok(())
    }

    /// Join a group call you were invited to.
    pub async fn join(&self, call_id: Uuid) -> Result<(), String> {
        let (local, members) = {
            let mut calls = self.calls.lock().unwrap();
            let entry = calls
                .get_mut(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            entry.members.insert(self.sender.local_uhid());
            (self.sender.local_uhid(), entry.members.clone())
        };
        for uhid in &members {
            if *uhid == local {
                continue;
            }
            let sig = GroupVoiceSignalingMessage {
                kind: "join".into(),
                call_id,
                from_uhid: local.clone(),
                to_uhid: uhid.clone(),
                invited_uhids: None,
                kicked_uhid: None,
                key_generation: None,
            };
            self.send_signaling(uhid, &sig).await;
        }
        Ok(())
    }

    /// Leave a group call.
    pub async fn leave(&self, call_id: Uuid) -> Result<(), String> {
        let (local, members) = {
            let mut calls = self.calls.lock().unwrap();
            let entry = calls
                .get_mut(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            entry.members.remove(&self.sender.local_uhid());
            (self.sender.local_uhid(), entry.members.clone())
        };
        for uhid in &members {
            let sig = GroupVoiceSignalingMessage {
                kind: "leave".into(),
                call_id,
                from_uhid: local.clone(),
                to_uhid: uhid.clone(),
                invited_uhids: None,
                kicked_uhid: None,
                key_generation: None,
            };
            self.send_signaling(uhid, &sig).await;
        }
        Ok(())
    }

    /// Kick a member from the call. Only the host may do this; enforced
    /// locally — the protocol relies on the host propagating the kick signal.
    pub async fn kick(
        &self,
        call_id: Uuid,
        target_uhid: &str,
    ) -> Result<(), String> {
        let (local, members) = {
            let mut calls = self.calls.lock().unwrap();
            let entry = calls
                .get_mut(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            if entry.host_uhid != self.sender.local_uhid() {
                return Err("only the host may kick members".into());
            }
            entry.members.remove(target_uhid);
            (self.sender.local_uhid(), entry.members.clone())
        };
        for uhid in &members {
            let sig = GroupVoiceSignalingMessage {
                kind: "kick".into(),
                call_id,
                from_uhid: local.clone(),
                to_uhid: uhid.clone(),
                invited_uhids: None,
                kicked_uhid: Some(target_uhid.to_string()),
                key_generation: None,
            };
            self.send_signaling(uhid, &sig).await;
        }
        Ok(())
    }

    /// Send an encoded audio frame to all current group members.
    ///
    /// Binary payload wire format:
    ///   [16] CallId (UUID RFC4122 big-endian)
    ///   [4]  Sequence (u32 little-endian)
    ///   [8]  TimestampMs (i64 little-endian)
    ///   [1]  IsSilence (0 or 1)
    ///   [4]  KeyGeneration (u32 little-endian)
    ///   [N]  EncodedPayload
    pub async fn send_frame(
        &self,
        call_id: Uuid,
        encoded_audio: &[u8],
        is_silence: bool,
        key_generation: u32,
    ) -> Result<(), String> {
        let members = {
            let calls = self.calls.lock().unwrap();
            let entry = calls
                .get(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            if !entry.active {
                return Err(format!("call {} is not active", call_id));
            }
            entry.members.clone()
        };

        let payload = build_group_voice_frame(call_id, encoded_audio, is_silence, key_generation);
        let local = self.sender.local_uhid();

        for uhid in &members {
            if *uhid == local {
                continue;
            }
            let mut pkt = MeshPacket::new(PacketType::VoiceCall, local.clone());
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
            PacketType::VoiceSignaling => self.handle_signaling(packet).await,
            PacketType::VoiceCall => self.handle_frame(packet).await,
            _ => Ok(()),
        }
    }

    /// Decode an inbound group-voice frame and surface it to application subscribers.
    /// Layout (build_group_voice_frame): [16] call_id | [4] seq LE | [8] ts LE |
    /// [1] silence | [4] key_generation LE | [N] audio.
    async fn handle_frame(&self, packet: &MeshPacket) -> Result<(), String> {
        let p = &packet.payload;
        if p.len() < 33 {
            return Err("group voice frame too short".to_string());
        }
        let call_id = Uuid::from_slice(&p[0..16]).map_err(|e| e.to_string())?;
        let seq = u32::from_le_bytes(p[16..20].try_into().unwrap());
        let timestamp_ms = i64::from_le_bytes(p[20..28].try_into().unwrap());
        let is_silence = p[28] != 0;
        let key_generation = u32::from_le_bytes(p[29..33].try_into().unwrap());
        let audio = p[33..].to_vec();
        let _ = self.frame_received_tx.send(GroupVoiceFrameReceivedEvent {
            call_id,
            sender_uhid: packet.source_uhid.clone(),
            seq,
            timestamp_ms,
            is_silence,
            key_generation,
            audio,
        });
        Ok(())
    }

    // ── private ────────────────────────────────────────────────────────────────

    async fn send_signaling(&self, to_uhid: &str, sig: &GroupVoiceSignalingMessage) {
        let body = match serde_json::to_vec(sig) {
            Ok(b) => b,
            Err(_) => return,
        };
        let route = self.routing.find_route(to_uhid).await;
        let next_hop = route
            .map(|r| r.next_hop_uhid)
            .unwrap_or_else(|| to_uhid.to_string());

        let mut pkt = MeshPacket::new(PacketType::VoiceSignaling, self.sender.local_uhid());
        pkt.destination_uhid = to_uhid.to_string();
        pkt.ttl = DEFAULT_TTL;
        pkt.priority = 32;
        pkt.payload = body;
        self.sender.send(&pkt, &next_hop).await;
    }

    async fn handle_signaling(&self, packet: &MeshPacket) -> Result<(), String> {
        let sig: GroupVoiceSignalingMessage = serde_json::from_slice(&packet.payload)
            .map_err(|e| format!("bad group signaling payload: {}", e))?;

        let local = self.sender.local_uhid();
        let mut calls = self.calls.lock().unwrap();
        match sig.kind.as_str() {
            "invite" => {
                let entry = calls.entry(sig.call_id).or_insert_with(|| GroupCallEntry {
                    call_id: sig.call_id,
                    host_uhid: packet.source_uhid.clone(),
                    members: HashSet::new(),
                    active: true,
                });
                if let Some(invited) = &sig.invited_uhids {
                    for m in invited {
                        entry.members.insert(m.clone());
                    }
                }
            }
            "join" => {
                if let Some(entry) = calls.get_mut(&sig.call_id) {
                    entry.members.insert(packet.source_uhid.clone());
                }
            }
            "leave" => {
                if let Some(entry) = calls.get_mut(&sig.call_id) {
                    entry.members.remove(&packet.source_uhid);
                }
            }
            "kick" => {
                if let Some(entry) = calls.get_mut(&sig.call_id) {
                    if let Some(kicked) = &sig.kicked_uhid {
                        entry.members.remove(kicked);
                        if kicked == &local {
                            entry.active = false;
                        }
                    }
                }
            }
            "end" => {
                if let Some(entry) = calls.get_mut(&sig.call_id) {
                    entry.active = false;
                }
            }
            _ => {}
        }
        Ok(())
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static GROUP_FRAME_SEQ: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

fn build_group_voice_frame(
    call_id: Uuid,
    encoded_audio: &[u8],
    is_silence: bool,
    key_generation: u32,
) -> Vec<u8> {
    let seq = GROUP_FRAME_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let ts = unix_millis();
    let mut buf = Vec::with_capacity(33 + encoded_audio.len());
    buf.extend_from_slice(call_id.as_bytes()); // [16]
    buf.extend_from_slice(&seq.to_le_bytes()); // [4]
    buf.extend_from_slice(&ts.to_le_bytes()); // [8]
    buf.push(if is_silence { 1 } else { 0 }); // [1]
    buf.extend_from_slice(&key_generation.to_le_bytes()); // [4]
    buf.extend_from_slice(encoded_audio); // [N]
    buf
}

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}
