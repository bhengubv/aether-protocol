// SPDX-License-Identifier: MIT

//! ERID-announce directed transport over [`PacketType::EridAnnounce`] (56).
//!
//! Binds `EridAnnounce(56)` to the mesh: a node shares its rotating-address
//! routing key with an established peer by directed-sending the (already
//! Signal-encrypted) announcement. Inbound announcements surface via an
//! [`EridAnnounceReceivedEvent`] (payload still encrypted) on a tokio broadcast
//! channel.
//!
//! TRANSPORT ONLY — the plaintext framing
//! ([`crate::identity::erid_announcement_codec`]) and the encryption (the host's
//! Signal service) are done by the host / EridExchangeService; this service just
//! carries the opaque encrypted blob as a directed packet and surfaces inbound
//! ones. Mirrors the C# `EridAnnounceService` and the Go / Python / TS / Kotlin /
//! Swift ports.

use std::sync::Arc;

use tokio::sync::broadcast;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};
use crate::routing::sender::MeshSender;

const ANNOUNCE_RECEIVED_CHANNEL_CAPACITY: usize = 64;

/// Emitted when an ERID announcement arrives from a peer (payload still
/// Signal-encrypted). Carries the opaque encrypted body plus the peer's UHID. The
/// host feeds [`Self::encrypted_announcement`] to its Signal service to decrypt,
/// then to [`crate::identity::erid_announcement_codec::try_decode`]. Mirrors the C#
/// `EridAnnounceReceived` event args.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EridAnnounceReceivedEvent {
    /// The packet body — a Signal-encrypted payload whose plaintext is an
    /// [`crate::identity::erid_announcement_codec`] frame.
    pub encrypted_announcement: Vec<u8>,
    /// UHID of the peer that sent the announcement (the packet source).
    pub from_uhid: String,
}

/// ERID-announce directed transport service. Directed send — never broadcast — so
/// a routing-key announcement does not leak to the whole mesh. Transport only: the
/// host encrypts the announcement (Signal) before handing it in, and consumes
/// received (still-encrypted) announcements out via the
/// [`EridAnnounceReceivedEvent`] broadcast.
pub struct EridAnnounceService {
    sender: Arc<dyn MeshSender>,

    /// Broadcast channel for announce-received events. Each subscriber receives an
    /// event the moment an inbound [`PacketType::EridAnnounce`] is accepted.
    announce_received_tx: broadcast::Sender<EridAnnounceReceivedEvent>,
}

impl EridAnnounceService {
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        let (announce_received_tx, _) = broadcast::channel(ANNOUNCE_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            announce_received_tx,
        }
    }

    /// Subscribe to announce-received events. Best-effort / fire-and-forget: events
    /// are dropped when there are no live receivers.
    pub fn subscribe_announce_received(&self) -> broadcast::Receiver<EridAnnounceReceivedEvent> {
        self.announce_received_tx.subscribe()
    }

    /// Send an encrypted ERID announcement directly to `peer_uhid` (directed
    /// [`PacketType::EridAnnounce`], TTL [`DEFAULT_TTL`]). Returns delivery success.
    /// Returns `false` without sending when `peer_uhid` or `encrypted` is empty.
    pub async fn send_announce(&self, peer_uhid: &str, encrypted: &[u8]) -> bool {
        if peer_uhid.is_empty() || encrypted.is_empty() {
            return false;
        }

        let mut packet = MeshPacket::new(PacketType::EridAnnounce, self.sender.local_uhid());
        packet.destination_uhid = peer_uhid.to_string();
        packet.ttl = DEFAULT_TTL;
        packet.payload = encrypted.to_vec();

        self.sender.send(&packet, peer_uhid).await
    }

    /// Process an inbound [`PacketType::EridAnnounce`]: emit an
    /// [`EridAnnounceReceivedEvent`] carrying the (still-encrypted) body. Returns
    /// `false` for the wrong packet type or an empty body; `true` once the
    /// announcement has been surfaced.
    pub async fn handle(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::EridAnnounce {
            return false;
        }
        if packet.payload.is_empty() {
            return false;
        }

        // Best-effort: deliver to any subscribers. Ignore SendError when there are
        // no live receivers (fire-and-forget).
        let _ = self.announce_received_tx.send(EridAnnounceReceivedEvent {
            encrypted_announcement: packet.payload.clone(),
            from_uhid: packet.source_uhid.clone(),
        });
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Re-pin the shared ERID-announcement frame byte-identity (existing 8/8 codec)
    // against fixtures/erid. Mirrors the C#
    // `EridAnnouncementCodec_MatchesCanonicalFrame` test: encode the routing key
    // from fixtures/erid/routing_key_hex with epoch_seconds=900, erid_length=16 and
    // assert the hex equals announcement_encode_hex.
    #[test]
    fn erid_announcement_codec_matches_canonical_frame() {
        use crate::identity::erid_announcement_codec;
        use std::path::PathBuf;

        let mut root = PathBuf::from(env!("CARGO_MANIFEST_DIR")); // .../aether-protocol/rust
        while !root.join("AetherNetProtocol.slnx").is_file() {
            assert!(root.pop(), "AetherNetProtocol.slnx not found above CARGO_MANIFEST_DIR");
        }
        let vectors_path = root.join("fixtures/erid/vectors.json");
        let doc: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(&vectors_path).unwrap()).unwrap();

        let routing_key = hex_decode(doc["routing_key_hex"].as_str().unwrap());
        let frame = erid_announcement_codec::encode(&routing_key, 900, 16).unwrap();
        let actual: String = frame.iter().map(|b| format!("{b:02x}")).collect();
        assert_eq!(actual, doc["announcement_encode_hex"].as_str().unwrap());
    }

    fn hex_decode(s: &str) -> Vec<u8> {
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
            .collect()
    }
}
