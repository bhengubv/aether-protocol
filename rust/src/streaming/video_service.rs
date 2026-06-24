// SPDX-License-Identifier: MIT

//! 1-to-1 video call service: signaling state machine + binary frame delivery.

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;
use crate::routing::RoutingService;
use tokio::sync::broadcast;

// ── Signaling wire type ────────────────────────────────────────────────────────

/// JSON signaling for video calls. Cross-language canonical wire format.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct VideoSignalingMessage {
    pub kind: String,
    pub call_id: Uuid,
    pub from_uhid: String,
    pub to_uhid: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub proposed_codecs: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub selected_codec: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub width: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub height: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fps: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bitrate_kbps: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

// ── Call state machine ─────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VideoCallState {
    Outgoing,
    Incoming,
    Connected,
    Ended,
    Failed,
}

#[derive(Debug, Clone)]
pub struct VideoCallEntry {
    pub call_id: Uuid,
    pub remote_uhid: String,
    pub state: VideoCallState,
}

// ── Service ───────────────────────────────────────────────────────────────────

/// Capacity of the inbound-frame broadcast channel; lagging subscribers drop oldest.
const FRAME_RECEIVED_CHANNEL_CAPACITY: usize = 256;

/// An inbound video/call frame decoded from a received VIDEO_FRAME/VIDEO_CALL packet,
/// delivered to subscribers of [`VideoCallService::subscribe_frames`].
#[derive(Debug, Clone)]
pub struct VideoFrameReceivedEvent {
    pub call_id: Uuid,
    pub sender_uhid: String,
    pub seq: u32,
    pub timestamp_ms: i64,
    pub is_keyframe: bool,
    pub data: Vec<u8>,
}

pub struct VideoCallService {
    sender: Arc<dyn MeshSender>,
    routing: Arc<RoutingService>,
    calls: Mutex<HashMap<Uuid, VideoCallEntry>>,
    frame_received_tx: broadcast::Sender<VideoFrameReceivedEvent>,
}

impl VideoCallService {
    pub fn new(sender: Arc<dyn MeshSender>, routing: Arc<RoutingService>) -> Self {
        let (frame_received_tx, _) = broadcast::channel(FRAME_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            routing,
            calls: Mutex::new(HashMap::new()),
            frame_received_tx,
        }
    }

    /// Subscribe to inbound video/call frames decoded from received VIDEO_FRAME /
    /// VIDEO_CALL packets.
    pub fn subscribe_frames(&self) -> broadcast::Receiver<VideoFrameReceivedEvent> {
        self.frame_received_tx.subscribe()
    }

    /// Originate a video call to `to_uhid`. Returns the new call-id.
    pub async fn send_offer(
        &self,
        to_uhid: &str,
        codecs: Vec<String>,
        width: u32,
        height: u32,
        fps: u32,
        bitrate_kbps: u32,
    ) -> Result<Uuid, String> {
        if to_uhid.is_empty() {
            return Err("to_uhid is empty".into());
        }
        let call_id = Uuid::new_v4();
        {
            let mut calls = self.calls.lock().unwrap();
            calls.insert(
                call_id,
                VideoCallEntry {
                    call_id,
                    remote_uhid: to_uhid.to_string(),
                    state: VideoCallState::Outgoing,
                },
            );
        }
        let sig = VideoSignalingMessage {
            kind: "offer".into(),
            call_id,
            from_uhid: self.sender.local_uhid(),
            to_uhid: to_uhid.to_string(),
            proposed_codecs: Some(codecs),
            selected_codec: None,
            width: Some(width),
            height: Some(height),
            fps: Some(fps),
            bitrate_kbps: Some(bitrate_kbps),
            reason: None,
        };
        self.send_signaling(to_uhid, &sig).await;
        Ok(call_id)
    }

    /// Accept an incoming video call.
    pub async fn accept_call(&self, call_id: Uuid) -> Result<(), String> {
        let remote_uhid = {
            let mut calls = self.calls.lock().unwrap();
            let entry = calls
                .get_mut(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            if entry.state != VideoCallState::Incoming {
                return Err(format!("call {} is not in Incoming state", call_id));
            }
            entry.state = VideoCallState::Connected;
            entry.remote_uhid.clone()
        };
        let sig = VideoSignalingMessage {
            kind: "answer".into(),
            call_id,
            from_uhid: self.sender.local_uhid(),
            to_uhid: remote_uhid.clone(),
            proposed_codecs: None,
            selected_codec: None,
            width: None,
            height: None,
            fps: None,
            bitrate_kbps: None,
            reason: None,
        };
        self.send_signaling(&remote_uhid, &sig).await;
        Ok(())
    }

    /// Hang up a video call.
    pub async fn hang_up(&self, call_id: Uuid) -> Result<(), String> {
        let remote_uhid = {
            let mut calls = self.calls.lock().unwrap();
            let entry = calls
                .get_mut(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            let prev = entry.state.clone();
            entry.state = VideoCallState::Ended;
            if matches!(prev, VideoCallState::Ended | VideoCallState::Failed) {
                return Ok(());
            }
            entry.remote_uhid.clone()
        };
        let sig = VideoSignalingMessage {
            kind: "hangup".into(),
            call_id,
            from_uhid: self.sender.local_uhid(),
            to_uhid: remote_uhid.clone(),
            proposed_codecs: None,
            selected_codec: None,
            width: None,
            height: None,
            fps: None,
            bitrate_kbps: None,
            reason: None,
        };
        self.send_signaling(&remote_uhid, &sig).await;
        Ok(())
    }

    /// Send an encoded video frame.
    ///
    /// Binary payload wire format:
    ///   [16] CallId (UUID RFC4122 big-endian)
    ///   [4]  Sequence (u32 little-endian)
    ///   [8]  TimestampMs (i64 little-endian)
    ///   [1]  IsKeyframe (0 or 1)
    ///   [N]  EncodedPayload
    pub async fn send_frame(
        &self,
        call_id: Uuid,
        encoded_video: &[u8],
        is_keyframe: bool,
    ) -> Result<(), String> {
        let remote_uhid = {
            let calls = self.calls.lock().unwrap();
            let entry = calls
                .get(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?;
            if entry.state != VideoCallState::Connected {
                return Err(format!("call {} is not connected", call_id));
            }
            entry.remote_uhid.clone()
        };
        let payload = build_video_frame(call_id, encoded_video, is_keyframe);

        let mut pkt = MeshPacket::new(PacketType::VideoFrame, self.sender.local_uhid());
        pkt.destination_uhid = remote_uhid.clone();
        pkt.ttl = DEFAULT_TTL;
        pkt.priority = 64;
        pkt.payload = payload;
        self.sender.send(&pkt, &remote_uhid).await;
        Ok(())
    }

    /// Request a keyframe from the remote peer.
    pub async fn request_keyframe(&self, call_id: Uuid) -> Result<(), String> {
        let remote_uhid = {
            let calls = self.calls.lock().unwrap();
            calls
                .get(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?
                .remote_uhid
                .clone()
        };
        let sig = VideoSignalingMessage {
            kind: "keyframe_request".into(),
            call_id,
            from_uhid: self.sender.local_uhid(),
            to_uhid: remote_uhid.clone(),
            proposed_codecs: None,
            selected_codec: None,
            width: None,
            height: None,
            fps: None,
            bitrate_kbps: None,
            reason: None,
        };
        self.send_signaling(&remote_uhid, &sig).await;
        Ok(())
    }

    /// Notify the remote peer of a quality change.
    pub async fn notify_quality_change(
        &self,
        call_id: Uuid,
        width: u32,
        height: u32,
        fps: u32,
        bitrate_kbps: u32,
    ) -> Result<(), String> {
        let remote_uhid = {
            let calls = self.calls.lock().unwrap();
            calls
                .get(&call_id)
                .ok_or_else(|| format!("unknown call_id {}", call_id))?
                .remote_uhid
                .clone()
        };
        let sig = VideoSignalingMessage {
            kind: "quality_change".into(),
            call_id,
            from_uhid: self.sender.local_uhid(),
            to_uhid: remote_uhid.clone(),
            proposed_codecs: None,
            selected_codec: None,
            width: Some(width),
            height: Some(height),
            fps: Some(fps),
            bitrate_kbps: Some(bitrate_kbps),
            reason: None,
        };
        self.send_signaling(&remote_uhid, &sig).await;
        Ok(())
    }

    /// Route an inbound packet to the right handler.
    pub async fn handle_packet(&self, packet: &MeshPacket) -> Result<(), String> {
        match packet.packet_type {
            PacketType::VideoSignaling => self.handle_signaling(packet).await,
            PacketType::VideoFrame | PacketType::VideoCall => self.handle_video_frame(packet).await,
            _ => Ok(()),
        }
    }

    /// Decode an inbound video/call frame and surface it to subscribers.
    /// Layout (build_video_frame): [16] call_id | [4] seq LE | [8] ts LE | [1] keyframe | [N] data.
    async fn handle_video_frame(&self, packet: &MeshPacket) -> Result<(), String> {
        let p = &packet.payload;
        if p.len() < 29 {
            return Err("video frame too short".to_string());
        }
        let call_id = Uuid::from_slice(&p[0..16]).map_err(|e| e.to_string())?;
        let seq = u32::from_le_bytes(p[16..20].try_into().unwrap());
        let timestamp_ms = i64::from_le_bytes(p[20..28].try_into().unwrap());
        let is_keyframe = p[28] != 0;
        let data = p[29..].to_vec();
        let _ = self.frame_received_tx.send(VideoFrameReceivedEvent {
            call_id,
            sender_uhid: packet.source_uhid.clone(),
            seq,
            timestamp_ms,
            is_keyframe,
            data,
        });
        Ok(())
    }

    // ── private ────────────────────────────────────────────────────────────────

    async fn send_signaling(&self, to_uhid: &str, sig: &VideoSignalingMessage) {
        let body = match serde_json::to_vec(sig) {
            Ok(b) => b,
            Err(_) => return,
        };
        let route = self.routing.find_route(to_uhid).await;
        let next_hop = route
            .map(|r| r.next_hop_uhid)
            .unwrap_or_else(|| to_uhid.to_string());

        let mut pkt = MeshPacket::new(PacketType::VideoSignaling, self.sender.local_uhid());
        pkt.destination_uhid = to_uhid.to_string();
        pkt.ttl = DEFAULT_TTL;
        pkt.priority = 32;
        pkt.payload = body;
        self.sender.send(&pkt, &next_hop).await;
    }

    async fn handle_signaling(&self, packet: &MeshPacket) -> Result<(), String> {
        let sig: VideoSignalingMessage = serde_json::from_slice(&packet.payload)
            .map_err(|e| format!("bad video signaling payload: {}", e))?;

        let mut calls = self.calls.lock().unwrap();
        match sig.kind.as_str() {
            "offer" => {
                calls.insert(
                    sig.call_id,
                    VideoCallEntry {
                        call_id: sig.call_id,
                        remote_uhid: packet.source_uhid.clone(),
                        state: VideoCallState::Incoming,
                    },
                );
            }
            "answer" => {
                if let Some(entry) = calls.get_mut(&sig.call_id) {
                    if entry.state == VideoCallState::Outgoing {
                        entry.state = VideoCallState::Connected;
                    }
                }
            }
            "hangup" => {
                if let Some(entry) = calls.get_mut(&sig.call_id) {
                    entry.state = VideoCallState::Ended;
                }
            }
            // keyframe_request and quality_change are surfaced to the app layer
            _ => {}
        }
        Ok(())
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static VIDEO_FRAME_SEQ: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

fn build_video_frame(call_id: Uuid, encoded_video: &[u8], is_keyframe: bool) -> Vec<u8> {
    let seq = VIDEO_FRAME_SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let ts = unix_millis();
    let mut buf = Vec::with_capacity(29 + encoded_video.len());
    buf.extend_from_slice(call_id.as_bytes()); // [16]
    buf.extend_from_slice(&seq.to_le_bytes()); // [4]
    buf.extend_from_slice(&ts.to_le_bytes()); // [8]
    buf.push(if is_keyframe { 1 } else { 0 }); // [1]
    buf.extend_from_slice(encoded_video); // [N]
    buf
}

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}
