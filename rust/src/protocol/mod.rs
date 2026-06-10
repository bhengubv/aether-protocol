// SPDX-License-Identifier: MIT

pub mod serializer;

use serde::{Deserialize, Serialize};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

/// Packet types in the Aether protocol
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum PacketType {
    RouteRequest = 1,
    RouteReply = 2,
    Data = 3,
    Ack = 4,
    SosBroadcast = 5,
    SosAck = 6,
    ChannelMessage = 7,
    ChunkRequest = 8,
    ChunkData = 9,
    Heartbeat = 10,
    StreamAnnounce = 11,
    StreamSegment = 12,
    StreamSubscribe = 13,
    StreamUnsubscribe = 14,
    VoicePtt = 15,
    VoiceCall = 16,
    VoiceSignaling = 17,
    DtnBundle = 18,
    DtnCustodyAck = 19,
    DtnDeliveryReceipt = 20,
    PresenceBeacon = 21,
    PresenceQuery = 22,
    ProfileSync = 23,
    TipPacket = 24,
    PreKeyRequest = 25,
    PreKeyResponse = 26,
    VideoCall = 27,
    VideoSignaling = 28,
    WatchSync = 29,
    WatchReaction = 30,
    VideoFrame = 31,
    ScreenShare = 32,
    WatchChunkRequest = 33,
    TorrentMetadata = 34,

    /// NamePublish — application-layer name resolution. Sent by directory
    /// service to announce a (name -> ContentDescriptor) binding to the mesh,
    /// or in response to an inbound NameQuery. Payload is a UTF-8 JSON-encoded
    /// NamePublishPayload. Added in v1.2.0 — closes Issue #60.
    NamePublish = 38,

    /// NameQuery — application-layer name resolution. Sent by directory
    /// service when resolve() misses the local cache; flooded across the mesh
    /// so any node holding the binding can reply with a NamePublish. Payload
    /// is a UTF-8 JSON-encoded NameQueryPayload. Added in v1.2.0 — closes
    /// Issue #60.
    NameQuery = 39,

    /// Capability handshake — sender announces supported protocol-version
    /// range + capability flags. Sent on first contact with an unknown peer.
    /// The payload is a UTF-8 JSON-encoded `HelloPayload`. Unauthenticated and
    /// unencrypted — peer identity is verified later via Ed25519 packet
    /// signatures.
    Hello = 50,

    /// Reply to a [`PacketType::Hello`] — receiver echoes back the agreed
    /// (highest mutually-supported) protocol version and the intersection of
    /// capability flags. Same JSON payload shape as [`PacketType::Hello`].
    HelloAck = 51,

    /// Gossip packet carrying a signed reputation-score delta for a target UHID.
    /// Payload is UTF-8 JSON-encoded [`crate::gossip::ReputationUpdatePayload`].
    ReputationUpdate = 52,

    /// Active bandwidth probe packet (ABMF W18-5).
    /// Payload carries a sequence number and probe size; the receiver replies
    /// with `BandwidthAck` embedding four timestamps for RTT computation.
    BandwidthProbe = 53,

    /// Bandwidth probe acknowledgement (ABMF W18-5).
    /// Payload is a serialised `BandwidthProbeAck` (four timestamps + probe size).
    BandwidthAck = 54,

    /// Mesh gossip payload carrying a BtlBw estimate (ABMF W18-5).
    /// Broadcast during handshake to warm-start the peer's estimator.
    /// Payload is a serialised `BandwidthGossipPayload`.
    BandwidthGossip = 55,
}

impl PacketType {
    pub fn from_byte(value: u8) -> Option<Self> {
        match value {
            1 => Some(PacketType::RouteRequest),
            2 => Some(PacketType::RouteReply),
            3 => Some(PacketType::Data),
            4 => Some(PacketType::Ack),
            5 => Some(PacketType::SosBroadcast),
            6 => Some(PacketType::SosAck),
            7 => Some(PacketType::ChannelMessage),
            8 => Some(PacketType::ChunkRequest),
            9 => Some(PacketType::ChunkData),
            10 => Some(PacketType::Heartbeat),
            11 => Some(PacketType::StreamAnnounce),
            12 => Some(PacketType::StreamSegment),
            13 => Some(PacketType::StreamSubscribe),
            14 => Some(PacketType::StreamUnsubscribe),
            15 => Some(PacketType::VoicePtt),
            16 => Some(PacketType::VoiceCall),
            17 => Some(PacketType::VoiceSignaling),
            18 => Some(PacketType::DtnBundle),
            19 => Some(PacketType::DtnCustodyAck),
            20 => Some(PacketType::DtnDeliveryReceipt),
            21 => Some(PacketType::PresenceBeacon),
            22 => Some(PacketType::PresenceQuery),
            23 => Some(PacketType::ProfileSync),
            24 => Some(PacketType::TipPacket),
            25 => Some(PacketType::PreKeyRequest),
            26 => Some(PacketType::PreKeyResponse),
            27 => Some(PacketType::VideoCall),
            28 => Some(PacketType::VideoSignaling),
            29 => Some(PacketType::WatchSync),
            30 => Some(PacketType::WatchReaction),
            31 => Some(PacketType::VideoFrame),
            32 => Some(PacketType::ScreenShare),
            33 => Some(PacketType::WatchChunkRequest),
            34 => Some(PacketType::TorrentMetadata),
            38 => Some(PacketType::NamePublish),
            39 => Some(PacketType::NameQuery),
            50 => Some(PacketType::Hello),
            51 => Some(PacketType::HelloAck),
            52 => Some(PacketType::ReputationUpdate),
            53 => Some(PacketType::BandwidthProbe),
            54 => Some(PacketType::BandwidthAck),
            55 => Some(PacketType::BandwidthGossip),
            _ => None,
        }
    }

    pub fn as_byte(&self) -> u8 {
        *self as u8
    }
}

/// Core mesh packet transmitted across the Aether network
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MeshPacket {
    /// Unique identifier for this packet
    pub id: Uuid,

    /// The type of packet
    pub packet_type: PacketType,

    /// Universal Hardware ID of the source node
    pub source_uhid: String,

    /// Universal Hardware ID of the destination node (empty for broadcast)
    pub destination_uhid: String,

    /// Time-to-live: decremented at each hop
    pub ttl: i32,

    /// Priority level (0 = normal, 999 = SOS)
    pub priority: u8,

    /// The packet payload
    pub payload: Vec<u8>,

    /// UTC timestamp when this packet was created (milliseconds since epoch)
    pub timestamp_ms: i64,

    /// Protocol version (1 = unsigned, 2 = signed)
    pub protocol_version: u8,

    /// Cryptographic signature over the packet contents
    pub signature: Vec<u8>,

    /// Random nonce to prevent replay attacks (8 bytes)
    pub packet_nonce: Vec<u8>,
}

impl MeshPacket {
    /// Creates a new mesh packet with default values
    pub fn new(packet_type: PacketType, source_uhid: String) -> Self {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_millis() as i64;

        MeshPacket {
            id: Uuid::new_v4(),
            packet_type,
            source_uhid,
            destination_uhid: String::new(),
            ttl: crate::constants::DEFAULT_TTL,
            priority: 0,
            payload: Vec::new(),
            timestamp_ms: now,
            protocol_version: crate::constants::PROTOCOL_VERSION_SIGNED,
            signature: Vec::new(),
            packet_nonce: Vec::new(),
        }
    }

    /// Returns true if this packet has exceeded the maximum allowed age
    pub fn is_expired(&self, max_age_seconds: u64) -> bool {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_millis() as i64;

        let age_ms = now - self.timestamp_ms;
        age_ms > (max_age_seconds * 1000) as i64
    }

    /// Returns true if the packet can still be forwarded
    pub fn can_forward(&self) -> bool {
        self.ttl > 0
    }

    /// Decrements the TTL and returns the new value
    pub fn decrement_ttl(&mut self) -> i32 {
        self.ttl = self.ttl.saturating_sub(1);
        self.ttl
    }

    /// Constructs the signable data for this packet (as per protocol spec)
    pub fn signable_data(&self) -> Vec<u8> {
        use sha2::{Digest, Sha256};

        let mut data = Vec::new();

        // PacketNonce (8 bytes)
        data.extend_from_slice(&self.packet_nonce);

        // TimestampMs (8 bytes, little-endian int64)
        data.extend_from_slice(&self.timestamp_ms.to_le_bytes());

        // Type (4 bytes, little-endian int32)
        data.extend_from_slice(&(self.packet_type.as_byte() as i32).to_le_bytes());

        // SourceUhidLength (4 bytes, little-endian int32)
        data.extend_from_slice(&(self.source_uhid.len() as i32).to_le_bytes());

        // SourceUhid (UTF-8 bytes)
        data.extend_from_slice(self.source_uhid.as_bytes());

        // DestinationUhidLength (4 bytes, little-endian int32)
        data.extend_from_slice(&(self.destination_uhid.len() as i32).to_le_bytes());

        // DestinationUhid (UTF-8 bytes)
        data.extend_from_slice(self.destination_uhid.as_bytes());

        // SHA-256(Payload) (32 bytes)
        let mut hasher = Sha256::new();
        hasher.update(&self.payload);
        data.extend_from_slice(&hasher.finalize());

        // Ttl (4 bytes, little-endian int32)
        data.extend_from_slice(&self.ttl.to_le_bytes());

        // Priority (4 bytes, little-endian int32)
        data.extend_from_slice(&(self.priority as i32).to_le_bytes());

        data
    }
}

impl std::fmt::Display for MeshPacket {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "[{:?}] {} src={} dst={} ttl={} pri={} ver={}",
            self.packet_type,
            self.id,
            self.source_uhid,
            self.destination_uhid,
            self.ttl,
            self.priority,
            self.protocol_version
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_packet_creation() {
        let packet = MeshPacket::new(PacketType::Data, "node-a".to_string());
        assert_eq!(packet.packet_type, PacketType::Data);
        assert_eq!(packet.source_uhid, "node-a");
        assert!(packet.can_forward());
    }

    #[test]
    fn test_ttl_decrement() {
        let mut packet = MeshPacket::new(PacketType::Data, "node-a".to_string());
        packet.ttl = 5;
        assert_eq!(packet.decrement_ttl(), 4);
        assert!(packet.can_forward());

        for _ in 0..4 {
            packet.decrement_ttl();
        }
        assert!(!packet.can_forward());
    }

    #[test]
    fn test_packet_type_conversion() {
        assert_eq!(PacketType::from_byte(1), Some(PacketType::RouteRequest));
        assert_eq!(PacketType::from_byte(3), Some(PacketType::Data));
        assert_eq!(PacketType::from_byte(23), Some(PacketType::ProfileSync));
        assert_eq!(PacketType::from_byte(50), Some(PacketType::Hello));
        assert_eq!(PacketType::from_byte(51), Some(PacketType::HelloAck));
        assert_eq!(PacketType::from_byte(99), None);
    }

    #[test]
    fn test_handshake_packet_type_byte_values() {
        // These wire bytes MUST match the C# PacketType.Hello/HelloAck values
        // — drift here breaks Rust↔C# handshake interop.
        assert_eq!(PacketType::Hello.as_byte(), 50);
        assert_eq!(PacketType::HelloAck.as_byte(), 51);
    }
}
