// SPDX-License-Identifier: MIT

//! 1-to-1 voice call service: signaling state machine + binary frame delivery.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;
use crate::routing::RoutingService;

// ── Signaling wire type ────────────────────────────────────────────────────────

/// JSON signaling message. Matches the cross-language canonical wire format.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct VoiceSignalingMessage {
    pub kind: String,
    pub call_id: Uuid,
    pub from_uhid: String,
    pub to_uhid: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub proposed_codecs: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub selected_codec: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sample_rate_hz: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

// ── Call state machine ─────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CallState {
    Outgoing,
    Incoming,
    Connected,
    Ended,
    Failed,
}

#[derive(Debug, Clone)]
pub struct CallEntry {
    pub call_id: Uuid,
    pub remote_uhid: String,
    pub state: CallState,
}

// ── Service ───────────────────────────────────────────────────────────────────

pub struct VoiceCallService {
    sender: Arc<dyn MeshSender>,
    routing: Arc<RoutingService>,
    calls: Mutex<HashMap<Uuid, CallEntry>>,
}

impl VoiceCallService {
    pub fn new(sender: Arc<dyn MeshSender>, routing: Arc<RoutingService>) -> Self {
        Self {
            sender,
            routing,
            calls: Mutex::new(HashMap::new()),
        }
    }

    /// Originate a call to `to_uhid`. Returns the new call-id.
    pub async fn send_offer(
        &self,
        to_uhid: &str,
        codecs: Vec<String>,
        sample_rate_hz: u32,
    ) -> Result<Uuid, String> {
        if to_uhid.is_empty() {
            return Err("to_uhid is empty".into());
        }
        let call_id = Uuid::new_v4();
        {
            let mut calls = self.calls.lock().unwrap();
            calls.insert(
                call_id,
                CallEntry {
                    call_id,
                    remote_uhid: to_uhid.to_string(),
                    state: CallState::Outgoing,
                },
            );
        }
        let sig = VoiceSignalingMessage {
            kind: "offer".into(),
            call_id,
            from_uhid: self.sender.local_uhid(),
            to_uhid: to_uhid.to_string(),
            proposed_codecs: Some(codecs),
            selected_codec: None,
            sample_rate_hz: Some(sample_rate_hz),
            reason: None,
        };
        self.send_signaling(to_uhid, &sig).await;
        Ok(call_id)
    }

    /// Accept an incoming call.
    pub async fn accept_call(&self, call_id: Uuid) -> Result<(), String> {
        let remote_uhid = {
            let mut calls = self.calls.lock().unwrap();
            let entry = calls
                .get_mut(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            if entry.state != CallState::Incoming {
                return Err(format!("call {} is not in Incoming state", call_id));
            }
            entry.state = CallState::Connected;
            entry.remote_uhid.clone()
        };
        let sig = VoiceSignalingMessage {
            kind: "answer".into(),
            call_id,
            from_uhid: self.sender.local_uhid(),
            to_uhid: remote_uhid.clone(),
            proposed_codecs: None,
            selected_codec: None,
            sample_rate_hz: None,
            reason: None,
        };
        self.send_signaling(&remote_uhid, &sig).await;
        Ok(())
    }

    /// Hang up (or cancel / reject) a call.
    pub async fn hang_up(&self, call_id: Uuid) -> Result<(), String> {
        let remote_uhid = {
            let mut calls = self.calls.lock().unwrap();
            let entry = calls
                .get_mut(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            let prev = entry.state.clone();
            entry.state = CallState::Ended;
            if matches!(prev, CallState::Ended | CallState::Failed) {
                return Ok(());
            }
            entry.remote_uhid.clone()
        };
        let kind = "hangup";
        let sig = VoiceSignalingMessage {
            kind: kind.into(),
            call_id,
            from_uhid: self.sender.local_uhid(),
            to_uhid: remote_uhid.clone(),
            proposed_codecs: None,
            selected_codec: None,
            sample_rate_hz: None,
            reason: None,
        };
        self.send_signaling(&remote_uhid, &sig).await;
        Ok(())
    }

    /// Send an encoded audio frame.
    ///
    /// Binary payload wire format:
    ///   [16] CallId (UUID RFC4122 big-endian)
    ///   [4]  Sequence (u32 little-endian)
    ///   [8]  TimestampMs (i64 little-endian)
    ///   [1]  IsSilence (0 or 1)
    ///   [N]  EncodedPayload
    pub async fn send_frame(
        &self,
        call_id: Uuid,
        encoded_audio: &[u8],
        is_silence: bool,
    ) -> Result<(), String> {
        let remote_uhid = {
            let calls = self.calls.lock().unwrap();
            let entry = calls
                .get(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            if entry.state != CallState::Connected {
                return Err(format!("call {} is not connected", call_id));
            }
            entry.remote_uhid.clone()
        };

        let payload = build_voice_frame(call_id, encoded_audio, is_silence);

        let mut pkt = MeshPacket::new(PacketType::VoiceCall, self.sender.local_uhid());
        pkt.destination_uhid = remote_uhid.clone();
        pkt.ttl = DEFAULT_TTL;
        pkt.priority = 64;
        pkt.payload = payload;
        self.sender.send(&pkt, &remote_uhid).await;
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

    // ── private ────────────────────────────────────────────────────────────────

    async fn send_signaling(&self, to_uhid: &str, sig: &VoiceSignalingMessage) {
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
        let sig: VoiceSignalingMessage = serde_json::from_slice(&packet.payload)
            .map_err(|e| format!("bad signaling payload: {}", e))?;

        let mut calls = self.calls.lock().unwrap();
        match sig.kind.as_str() {
            "offer" => {
                calls.insert(
                    sig.call_id,
                    CallEntry {
                        call_id: sig.call_id,
                        remote_uhid: packet.source_uhid.clone(),
                        state: CallState::Incoming,
                    },
                );
            }
            "answer" => {
                if let Some(entry) = calls.get_mut(&sig.call_id) {
                    if entry.state == CallState::Outgoing {
                        entry.state = CallState::Connected;
                    }
                }
            }
            "hangup" | "cancel" | "timeout" => {
                if let Some(entry) = calls.get_mut(&sig.call_id) {
                    entry.state = CallState::Ended;
                }
            }
            _ => {}
        }
        Ok(())
    }

    async fn handle_frame(&self, _packet: &MeshPacket) -> Result<(), String> {
        // Frames are surfaced to the application layer; the service itself just validates.
        Ok(())
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static FRAME_SEQ: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

fn build_voice_frame(call_id: Uuid, encoded_audio: &[u8], is_silence: bool) -> Vec<u8> {
    let seq = FRAME_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let ts = unix_millis();
    let mut buf = Vec::with_capacity(29 + encoded_audio.len());
    buf.extend_from_slice(call_id.as_bytes()); // [16] big-endian bytes
    buf.extend_from_slice(&seq.to_le_bytes()); // [4]
    buf.extend_from_slice(&ts.to_le_bytes()); // [8]
    buf.push(if is_silence { 1 } else { 0 }); // [1]
    buf.extend_from_slice(encoded_audio); // [N]
    buf
}

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}
