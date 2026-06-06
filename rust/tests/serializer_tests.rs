// SPDX-License-Identifier: MIT
//! Round-trip tests for the binary `PacketSerializer`.
//!
//! Mirror of `swift/Tests/PacketSerializationTests.swift`. Cross-language byte
//! equivalence is anchored separately under `fixtures/`.

use uuid::Uuid;

use aethermesh_protocol::protocol::{
    serializer::PacketSerializer, MeshPacket, PacketType,
};

fn nonce(fill: u8) -> Vec<u8> {
    vec![fill; 8]
}

fn fresh(packet_type: PacketType, src: &str) -> MeshPacket {
    let mut p = MeshPacket::new(packet_type, src.to_string());
    p.packet_nonce = nonce(0);
    p
}

#[test]
fn round_trip_preserves_all_fields() {
    let mut p = MeshPacket::new(PacketType::Data, "alice-node".to_string());
    p.destination_uhid = "bob-node".to_string();
    p.ttl = 7;
    p.priority = 10;
    p.payload = b"Hello, Aether!".to_vec();
    p.packet_nonce = nonce(0xAB);
    p.timestamp_ms = 1_710_528_000_000;

    let bytes = PacketSerializer::serialize(&p).unwrap();
    let got = PacketSerializer::deserialize(&bytes).unwrap();

    assert_eq!(got.packet_type, p.packet_type);
    assert_eq!(got.source_uhid, p.source_uhid);
    assert_eq!(got.destination_uhid, p.destination_uhid);
    assert_eq!(got.ttl, p.ttl);
    assert_eq!(got.priority, p.priority);
    assert_eq!(got.payload, p.payload);
    assert_eq!(got.packet_nonce, p.packet_nonce);
    assert_eq!(got.protocol_version, p.protocol_version);
}

#[test]
fn empty_destination_uhid_round_trips() {
    let mut p = fresh(PacketType::SosBroadcast, "node-1");
    p.destination_uhid = String::new();
    let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
    assert_eq!(got.source_uhid, "node-1");
    assert_eq!(got.destination_uhid, "");
}

#[test]
fn empty_payload_round_trips() {
    let mut p = fresh(PacketType::Heartbeat, "node-1");
    p.payload = Vec::new();
    let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
    assert_eq!(got.payload.len(), 0);
}

#[test]
fn large_payload_round_trips() {
    let mut p = fresh(PacketType::ChunkData, "node-1");
    p.destination_uhid = "node-2".to_string();
    p.payload = vec![0xFF; 262_144];
    let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
    assert_eq!(got.payload.len(), 262_144);
    assert_eq!(got.payload[0], 0xFF);
    assert_eq!(got.payload[262_143], 0xFF);
}

#[test]
fn uuid_round_trips() {
    let expected = Uuid::parse_str("550e8400-e29b-41d4-a716-446655440000").unwrap();
    let mut p = fresh(PacketType::Data, "node-1");
    p.id = expected;
    let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
    assert_eq!(got.id, expected);
}

#[test]
fn uuid_wire_order_is_rfc4122_big_endian() {
    let expected = Uuid::parse_str("550e8400-e29b-41d4-a716-446655440000").unwrap();
    let mut p = fresh(PacketType::Data, "n");
    p.id = expected;
    let bytes = PacketSerializer::serialize(&p).unwrap();
    let want = [
        0x55, 0x0e, 0x84, 0x00, 0xe2, 0x9b, 0x41, 0xd4,
        0xa7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x00,
    ];
    assert_eq!(&bytes[2..18], &want);
}

#[test]
fn too_short_returns_error() {
    assert!(PacketSerializer::deserialize(&[0x01, 0x02]).is_err());
}

#[test]
fn try_deserialize_returns_none_on_garbage() {
    assert!(PacketSerializer::try_deserialize(&[0xFF]).is_none());
}

#[test]
fn all_packet_types_round_trip() {
    use PacketType::*;
    for t in [
        RouteRequest, RouteReply, Data, Ack, SosBroadcast, SosAck,
        ChannelMessage, ChunkRequest, ChunkData, Heartbeat,
        StreamAnnounce, StreamSegment, StreamSubscribe, StreamUnsubscribe,
        VoicePtt, VoiceCall, VoiceSignaling, DtnBundle, DtnCustodyAck,
        DtnDeliveryReceipt, PresenceBeacon, PresenceQuery, ProfileSync,
        TipPacket, PreKeyRequest, PreKeyResponse, VideoCall, VideoSignaling,
        WatchSync, WatchReaction, VideoFrame, ScreenShare, WatchChunkRequest,
        TorrentMetadata,
    ] {
        let p = fresh(t, "n");
        let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
        assert_eq!(got.packet_type, t);
    }
}

#[test]
fn timestamp_preserved_to_ms() {
    let mut p = fresh(PacketType::Data, "node-1");
    p.timestamp_ms = 1_710_528_000_000;
    let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
    assert_eq!(got.timestamp_ms, 1_710_528_000_000);
}

#[test]
fn unicode_uhids_round_trip() {
    let mut p = fresh(PacketType::Data, "노드-1");
    p.destination_uhid = "узел-2".to_string();
    let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
    assert_eq!(got.source_uhid, "노드-1");
    assert_eq!(got.destination_uhid, "узел-2");
}

#[test]
fn signature_preserved() {
    let mut p = fresh(PacketType::Data, "node-1");
    p.signature = vec![0xAB; 64];
    let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
    assert_eq!(got.signature, vec![0xAB; 64]);
}

#[test]
fn ttl_full_i32_range_preserved() {
    // Anchors the i32-TTL contract — would have wrapped to 0 under the
    // pre-2026-05-02 u8 bug.
    let mut p = fresh(PacketType::Data, "n");
    p.ttl = 256;
    let got = PacketSerializer::deserialize(&PacketSerializer::serialize(&p).unwrap()).unwrap();
    assert_eq!(got.ttl, 256);
}
