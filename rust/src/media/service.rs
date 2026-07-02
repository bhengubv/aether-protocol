// SPDX-License-Identifier: MIT

//! Mesh bindings for the VoicePtt(15) + ScreenShare(32) media frames.
//!
//! Binds [`PacketType::VoicePtt`] (15) and [`PacketType::ScreenShare`] (32) to
//! the mesh: each service directed-sends a binary media frame to a single peer
//! ([`VoicePttService::send_frame`] / [`ScreenShareService::send_frame`]) and
//! surfaces inbound frames via a tokio broadcast channel
//! ([`VoicePttFrameReceivedEvent`] / [`ScreenShareFrameReceivedEvent`]) carrying
//! the decoded frame plus the source peer's UHID.
//!
//! Directed send — never broadcast — so a media frame goes only to the call
//! peer. Mirrors the C# `VoicePttService` / `ScreenShareService` and the Go /
//! Python / TS / Kotlin / Swift ports.

use std::sync::Arc;

use tokio::sync::broadcast;

use crate::constants::DEFAULT_TTL;
use crate::media::codec::{
    self, ScreenShareFrame, VoicePttFrame,
};
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const FRAME_RECEIVED_CHANNEL_CAPACITY: usize = 64;

/// Emitted when a VoicePtt frame arrives from a peer. Carries the decoded frame
/// plus the peer's UHID (the packet source). Mirrors the C#
/// `VoicePttFrameReceived` event args.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VoicePttFrameReceivedEvent {
    pub frame: VoicePttFrame,
    pub from_uhid: String,
}

/// Emitted when a ScreenShare frame arrives from a peer. Carries the decoded
/// frame plus the peer's UHID (the packet source). Mirrors the C#
/// `ScreenShareFrameReceived` event args.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ScreenShareFrameReceivedEvent {
    pub frame: ScreenShareFrame,
    pub from_uhid: String,
}

/// Binds [`PacketType::VoicePtt`] (15) to the mesh: directed push-to-talk audio
/// frames + inbound frame event.
pub struct VoicePttService {
    sender: Arc<dyn MeshSender>,
    frame_received_tx: broadcast::Sender<VoicePttFrameReceivedEvent>,
}

impl VoicePttService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (frame_received_tx, _) = broadcast::channel(FRAME_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            frame_received_tx,
        }
    }

    /// Subscribe to frame-received events. Best-effort / fire-and-forget: events
    /// are dropped when there are no live receivers.
    pub fn subscribe_frame_received(&self) -> broadcast::Receiver<VoicePttFrameReceivedEvent> {
        self.frame_received_tx.subscribe()
    }

    /// Directed-send a push-to-talk audio `frame` to `peer_uhid` (directed
    /// [`PacketType::VoicePtt`], TTL [`DEFAULT_TTL`]). Returns delivery success.
    /// Returns `false` without sending when `peer_uhid` is empty.
    pub async fn send_frame(&self, peer_uhid: &str, frame: &VoicePttFrame) -> bool {
        if peer_uhid.is_empty() {
            return false;
        }
        let mut packet = MeshPacket::new(PacketType::VoicePtt, self.sender.local_uhid());
        packet.destination_uhid = peer_uhid.to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = codec::serialize_voice_ptt(frame);
        self.sender.send(&packet, peer_uhid).await
    }

    /// Process an inbound [`PacketType::VoicePtt`]: decode the frame and emit a
    /// [`VoicePttFrameReceivedEvent`]. Returns `false` for the wrong packet type
    /// or a malformed (short, `< 29`-byte) body; `true` once the frame has been
    /// surfaced.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::VoicePtt {
            return false;
        }
        let frame = match codec::deserialize_voice_ptt(&packet.payload) {
            Some(f) => f,
            None => return false,
        };
        let _ = self.frame_received_tx.send(VoicePttFrameReceivedEvent {
            frame,
            from_uhid: packet.source_uhid.clone(),
        });
        true
    }
}

/// Binds [`PacketType::ScreenShare`] (32) to the mesh: directed screen-share
/// video frames + inbound frame event.
pub struct ScreenShareService {
    sender: Arc<dyn MeshSender>,
    frame_received_tx: broadcast::Sender<ScreenShareFrameReceivedEvent>,
}

impl ScreenShareService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (frame_received_tx, _) = broadcast::channel(FRAME_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            frame_received_tx,
        }
    }

    /// Subscribe to frame-received events. Best-effort / fire-and-forget: events
    /// are dropped when there are no live receivers.
    pub fn subscribe_frame_received(&self) -> broadcast::Receiver<ScreenShareFrameReceivedEvent> {
        self.frame_received_tx.subscribe()
    }

    /// Directed-send a screen-share video `frame` to `peer_uhid` (directed
    /// [`PacketType::ScreenShare`], TTL [`DEFAULT_TTL`]). Returns delivery
    /// success. Returns `false` without sending when `peer_uhid` is empty.
    pub async fn send_frame(&self, peer_uhid: &str, frame: &ScreenShareFrame) -> bool {
        if peer_uhid.is_empty() {
            return false;
        }
        let mut packet = MeshPacket::new(PacketType::ScreenShare, self.sender.local_uhid());
        packet.destination_uhid = peer_uhid.to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = codec::serialize_screen_share(frame);
        self.sender.send(&packet, peer_uhid).await
    }

    /// Process an inbound [`PacketType::ScreenShare`]: decode the frame and emit
    /// a [`ScreenShareFrameReceivedEvent`]. Returns `false` for the wrong packet
    /// type or a malformed (short, `< 29`-byte) body; `true` once the frame has
    /// been surfaced.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::ScreenShare {
            return false;
        }
        let frame = match codec::deserialize_screen_share(&packet.payload) {
            Some(f) => f,
            None => return false,
        };
        let _ = self.frame_received_tx.send(ScreenShareFrameReceivedEvent {
            frame,
            from_uhid: packet.source_uhid.clone(),
        });
        true
    }
}
