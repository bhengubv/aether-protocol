// SPDX-License-Identifier: MIT

//! Directed video call-control over [`PacketType::VideoCall`].
//!
//! Ring/accept/decline/hangup signalling between two peers. The caller rings a
//! peer (minting a call id); either side then accepts, declines, or hangs up.
//! Each control verb is a directed [`PacketType::VideoCall`] send to the peer;
//! inbound signals surface via a [`VideoCallStateChangedEvent`]. The media plane
//! (SDP/ICE + frames) is handled separately by the streaming `VideoCallService`.
//! Mirrors the C# `VideoCallControlService` and the Go / Python / TS / Kotlin /
//! Swift ports.

use serde::{Deserialize, Serialize};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::sync::broadcast;
use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const CALL_STATE_CHANGED_CHANNEL_CAPACITY: usize = 64;

/// JSON payload for [`PacketType::VideoCall`] packets — the video call-control
/// signal (ring / accept / decline / hangup), distinct from the media-plane
/// `VideoSignaling` (SDP/ICE) and `VideoFrame` (media) handled by the streaming
/// `VideoCallService`. This is the caller-intent layer, mirroring how `VoiceCall`
/// carries voice call-control.
///
/// Wire format: UTF-8 JSON, snake_case keys, field order `call_id`, `action`,
/// `sent_at_ms`, no whitespace, `call_id` a lowercase-dashed UUID, `sent_at_ms` a
/// bare integer, `action` an ASCII verb. Byte-identical across all eight language
/// ports — see `fixtures/videocall/vectors.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct VideoCallControlWire {
    /// Unique id for this call (minted by the caller on ring; echoed by accept/decline/hangup).
    call_id: Uuid,
    /// Control verb: "ring", "accept", "decline", or "hangup".
    action: String,
    /// Unix timestamp in milliseconds when the control signal was sent.
    sent_at_ms: i64,
}

/// Event emitted when a video call-control signal arrives from a peer. Mirrors the
/// C# `VideoCallStateChanged` event payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VideoCallStateChangedEvent {
    /// Id of the call the signal refers to.
    pub call_id: Uuid,
    /// The control verb received ("ring" / "accept" / "decline" / "hangup").
    pub action: String,
    /// UHID of the peer that sent the signal.
    pub from_uhid: String,
}

/// Directed video call-control service. Sends directed [`PacketType::VideoCall`]
/// signals (ring/accept/decline/hangup) and surfaces inbound ones via a
/// [`VideoCallStateChangedEvent`].
pub struct VideoCallControlService {
    sender: Arc<dyn MeshSender>,

    /// Broadcast channel for call-state-changed events. Each subscriber receives
    /// an event the moment an inbound [`PacketType::VideoCall`] signal is accepted.
    state_changed_tx: broadcast::Sender<VideoCallStateChangedEvent>,
}

impl VideoCallControlService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (state_changed_tx, _) = broadcast::channel(CALL_STATE_CHANGED_CHANNEL_CAPACITY);
        Self {
            sender,
            state_changed_tx,
        }
    }

    /// Subscribe to call-state-changed events. Each subscriber receives an event
    /// the moment an inbound [`PacketType::VideoCall`] signal is accepted.
    /// Best-effort / fire-and-forget: events are dropped when there are no live
    /// receivers.
    pub fn subscribe_state_changed(&self) -> broadcast::Receiver<VideoCallStateChangedEvent> {
        self.state_changed_tx.subscribe()
    }

    /// Ring `peer_uhid`: mint a call id and send a directed "ring". Returns the new
    /// call id.
    pub async fn ring(&self, peer_uhid: &str) -> Uuid {
        let call_id = Uuid::new_v4();
        self.send_control(call_id, peer_uhid, "ring").await;
        call_id
    }

    /// Send a directed "accept" for `call_id` to `peer_uhid`. Returns delivery success.
    pub async fn accept(&self, call_id: Uuid, peer_uhid: &str) -> bool {
        self.send_control(call_id, peer_uhid, "accept").await
    }

    /// Send a directed "decline" for `call_id` to `peer_uhid`. Returns delivery success.
    pub async fn decline(&self, call_id: Uuid, peer_uhid: &str) -> bool {
        self.send_control(call_id, peer_uhid, "decline").await
    }

    /// Send a directed "hangup" for `call_id` to `peer_uhid`. Returns delivery success.
    pub async fn hangup(&self, call_id: Uuid, peer_uhid: &str) -> bool {
        self.send_control(call_id, peer_uhid, "hangup").await
    }

    async fn send_control(&self, call_id: Uuid, peer_uhid: &str, action: &str) -> bool {
        let body = serde_json::to_vec(&VideoCallControlWire {
            call_id,
            action: action.to_string(),
            sent_at_ms: unix_millis(),
        })
        .unwrap_or_default();

        let mut packet = MeshPacket::new(PacketType::VideoCall, self.sender.local_uhid());
        packet.destination_uhid = peer_uhid.to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        self.sender.send(&packet, peer_uhid).await
    }

    /// Process an incoming [`PacketType::VideoCall`] packet: parse it and emit a
    /// [`VideoCallStateChangedEvent`]. Returns `false` for the wrong packet type, a
    /// malformed payload, or an empty action; `true` once the signal has been
    /// surfaced.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::VideoCall {
            return false;
        }

        let body: VideoCallControlWire = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };
        if body.action.is_empty() {
            return false;
        }

        // Best-effort: deliver to any subscribers. Ignore SendError when there are
        // no live receivers (fire-and-forget).
        let _ = self.state_changed_tx.send(VideoCallStateChangedEvent {
            call_id: body.call_id,
            action: body.action,
            from_uhid: packet.source_uhid.clone(),
        });
        true
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

    // Byte-identity gate: `VideoCallControlWire` must serialize to exactly these
    // bytes in every language (fixtures/videocall/vectors.json). snake_case keys,
    // field order call_id, action, sent_at_ms, no whitespace, UUID lowercase-dashed,
    // sent_at_ms a bare integer. Mirrors the C#
    // `VideoCallControlPayload_SerializesToCanonicalBytes` (both InlineData vectors).
    #[test]
    fn video_call_control_payload_serializes_to_canonical_bytes() {
        let cases = [
            (
                "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
                "ring",
                1_700_000_000_000i64,
                "{\"call_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"action\":\"ring\",\"sent_at_ms\":1700000000000}",
            ),
            (
                "00000000-0000-0000-0000-000000000000",
                "hangup",
                0i64,
                "{\"call_id\":\"00000000-0000-0000-0000-000000000000\",\"action\":\"hangup\",\"sent_at_ms\":0}",
            ),
        ];

        for (call_id, action, sent_at_ms, expected) in cases {
            let payload = VideoCallControlWire {
                call_id: Uuid::parse_str(call_id).unwrap(),
                action: action.to_string(),
                sent_at_ms,
            };
            let bytes = serde_json::to_vec(&payload).unwrap();
            let json = String::from_utf8(bytes).unwrap();
            assert_eq!(
                json, expected,
                "byte-identity mismatch for call_id={call_id} action={action}"
            );
        }
    }
}
