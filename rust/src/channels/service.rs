// SPDX-License-Identifier: MIT

//! Named-channel pub/sub over [`PacketType::ChannelMessage`].
//!
//! A node subscribes to the channel ids it cares about; publishing floods a
//! [`PacketType::ChannelMessage`] to the whole mesh; subscribed receivers surface
//! the message via a [`ChannelMessageReceivedEvent`]. Messages are de-duplicated
//! by their message id and re-flooded (TTL-bounded) so they reach subscribers
//! several hops away. Mirrors the C# `ChannelMessageService` and the Go / Python /
//! TS / Kotlin / Swift ports.

use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::sync::broadcast;
use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const CHANNEL_MESSAGE_CHANNEL_CAPACITY: usize = 64;

/// JSON payload for [`PacketType::ChannelMessage`] packets. Wire format: UTF-8
/// JSON with snake_case keys, field order `channel_id`, `message_id`,
/// `sender_uhid`, `content`, `sent_at_ms`, no whitespace, `message_id` a
/// lowercase-dashed UUID, `sent_at_ms` a bare integer. Byte-identical across all
/// eight language ports — see `fixtures/channels/vectors.json`.
///
/// A named channel is an application-layer pub/sub topic ("res-floor-3", a
/// society, a project team). The original author is carried in `sender_uhid` so
/// it survives relay hops (the enclosing packet's `source_uhid` changes at each
/// hop).
#[derive(Debug, Clone, Serialize, Deserialize)]
struct ChannelMessageWire {
    /// Application-defined channel identifier (opaque to the protocol).
    channel_id: String,
    /// Unique id for this message — used for flood de-duplication.
    message_id: Uuid,
    /// UHID of the original author (preserved across relay hops).
    sender_uhid: String,
    /// Message body.
    content: String,
    /// Unix timestamp in milliseconds when the author published the message.
    sent_at_ms: i64,
}

/// Event emitted when a channel message arrives on a channel this node is
/// subscribed to (never emitted for this node's own messages). Mirrors the C#
/// `ChannelMessageReceived` event payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChannelMessageReceivedEvent {
    /// Channel the message was published to.
    pub channel_id: String,
    /// Unique id of the message.
    pub message_id: Uuid,
    /// UHID of the original author.
    pub sender_uhid: String,
    /// Message body.
    pub content: String,
    /// Unix-ms timestamp the author published the message.
    pub sent_at_ms: i64,
}

/// Named-channel pub/sub service. Publishing floods a
/// [`PacketType::ChannelMessage`]; receivers de-dup by message id, surface
/// messages for subscribed channels, and re-flood (TTL-bounded) so the message
/// reaches subscribers multiple hops away.
pub struct ChannelMessageService {
    sender: Arc<dyn MeshSender>,
    state: Mutex<ChannelState>,

    /// Broadcast channel for channel-message-received events. Each subscriber
    /// receives an event the moment a message on a subscribed channel is
    /// accepted (and it is not this node's own).
    received_tx: broadcast::Sender<ChannelMessageReceivedEvent>,
}

struct ChannelState {
    /// Channels this node is currently subscribed to.
    subscriptions: HashSet<String>,
    /// Message ids already processed — for flood de-duplication.
    seen: HashSet<Uuid>,
}

impl ChannelMessageService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (received_tx, _) = broadcast::channel(CHANNEL_MESSAGE_CHANNEL_CAPACITY);
        Self {
            sender,
            state: Mutex::new(ChannelState {
                subscriptions: HashSet::new(),
                seen: HashSet::new(),
            }),
            received_tx,
        }
    }

    /// Subscribe to a channel — messages on it will emit a
    /// [`ChannelMessageReceivedEvent`]. An empty channel id is ignored.
    pub fn subscribe(&self, channel_id: &str) {
        if channel_id.is_empty() {
            return;
        }
        let mut state = self.state.lock().unwrap();
        state.subscriptions.insert(channel_id.to_string());
    }

    /// Stop surfacing messages for a channel.
    pub fn unsubscribe(&self, channel_id: &str) {
        let mut state = self.state.lock().unwrap();
        state.subscriptions.remove(channel_id);
    }

    /// The channels this node is currently subscribed to.
    pub fn get_subscriptions(&self) -> Vec<String> {
        let state = self.state.lock().unwrap();
        state.subscriptions.iter().cloned().collect()
    }

    /// Subscribe to channel-message-received events. Each subscriber receives an
    /// event the moment a message on a subscribed channel is accepted (and it is
    /// not this node's own). Best-effort / fire-and-forget: events are dropped
    /// when there are no live receivers.
    pub fn subscribe_received(&self) -> broadcast::Receiver<ChannelMessageReceivedEvent> {
        self.received_tx.subscribe()
    }

    /// Publish `content` to `channel_id`: floods a [`PacketType::ChannelMessage`]
    /// (`destination_uhid` `*`, TTL [`DEFAULT_TTL`]) to all peers. Seeds the dedup
    /// set with its own message id so the message is never re-handled when it
    /// floods back. Returns the number of peers reached directly. An empty
    /// `channel_id` is rejected (returns 0).
    pub async fn publish(&self, channel_id: &str, content: &str) -> usize {
        if channel_id.is_empty() {
            return 0;
        }

        let message_id = Uuid::new_v4();
        let body = serde_json::to_vec(&ChannelMessageWire {
            channel_id: channel_id.to_string(),
            message_id,
            sender_uhid: self.sender.local_uhid(),
            content: content.to_string(),
            sent_at_ms: unix_millis(),
        })
        .unwrap_or_default();

        // Never re-handle our own message when it floods back.
        {
            let mut state = self.state.lock().unwrap();
            state.seen.insert(message_id);
        }

        let mut packet = MeshPacket::new(PacketType::ChannelMessage, self.sender.local_uhid());
        packet.destination_uhid = "*".to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = body;

        self.sender.broadcast(&packet).await
    }

    /// Process an incoming [`PacketType::ChannelMessage`] packet: de-dup by
    /// message id, surface it (emit a [`ChannelMessageReceivedEvent`]) if we are
    /// subscribed to its channel and it is not our own, and re-flood while TTL
    /// allows and it is not our own. Returns `false` for the wrong packet type, a
    /// malformed payload, or a duplicate; `true` once the message has been
    /// processed.
    pub async fn handle(&self, packet: &mut MeshPacket) -> bool {
        if packet.packet_type != PacketType::ChannelMessage {
            return false;
        }

        let body: ChannelMessageWire = match serde_json::from_slice(&packet.payload) {
            Ok(b) => b,
            Err(_) => return false,
        };
        if body.channel_id.is_empty() {
            return false;
        }

        // Flood de-duplication: only the first copy of a given message id is processed.
        {
            let mut state = self.state.lock().unwrap();
            if !state.seen.insert(body.message_id) {
                return false;
            }
        }

        let is_own = body.sender_uhid == self.sender.local_uhid();
        let is_subscribed = {
            let state = self.state.lock().unwrap();
            state.subscriptions.contains(&body.channel_id)
        };

        if !is_own && is_subscribed {
            // Best-effort: deliver to any subscribers. Ignore SendError when
            // there are no live receivers (fire-and-forget).
            let _ = self.received_tx.send(ChannelMessageReceivedEvent {
                channel_id: body.channel_id.clone(),
                message_id: body.message_id,
                sender_uhid: body.sender_uhid.clone(),
                content: body.content.clone(),
                sent_at_ms: body.sent_at_ms,
            });
        }

        // Re-flood so subscribers further out receive it — even if WE aren't
        // subscribed (pure relay).
        if packet.ttl > 1 && !is_own {
            packet.ttl -= 1;
            self.sender.broadcast(packet).await;
        }

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

    // Byte-identity gate: `ChannelMessageWire` must serialize to exactly these
    // bytes in every language (fixtures/channels/vectors.json). snake_case keys,
    // field order channel_id, message_id, sender_uhid, content, sent_at_ms, no
    // whitespace, UUID lowercase-dashed, sent_at_ms a bare integer. Mirrors the C#
    // `ChannelMessagePayload_SerializesToCanonicalBytes` (both InlineData vectors).
    #[test]
    fn channel_message_payload_serializes_to_canonical_bytes() {
        let cases = [
            (
                "res-floor-3",
                "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
                "aether:alice:01",
                "meeting at 6",
                1_700_000_000_000i64,
                "{\"channel_id\":\"res-floor-3\",\"message_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"sender_uhid\":\"aether:alice:01\",\"content\":\"meeting at 6\",\"sent_at_ms\":1700000000000}",
            ),
            (
                "g",
                "00000000-0000-0000-0000-000000000000",
                "n",
                "",
                0i64,
                "{\"channel_id\":\"g\",\"message_id\":\"00000000-0000-0000-0000-000000000000\",\"sender_uhid\":\"n\",\"content\":\"\",\"sent_at_ms\":0}",
            ),
        ];

        for (channel_id, message_id, sender_uhid, content, sent_at_ms, expected) in cases {
            let payload = ChannelMessageWire {
                channel_id: channel_id.to_string(),
                message_id: Uuid::parse_str(message_id).unwrap(),
                sender_uhid: sender_uhid.to_string(),
                content: content.to_string(),
                sent_at_ms,
            };
            let bytes = serde_json::to_vec(&payload).unwrap();
            let json = String::from_utf8(bytes).unwrap();
            assert_eq!(
                json, expected,
                "byte-identity mismatch for channel_id={channel_id} message_id={message_id}"
            );
        }
    }
}
