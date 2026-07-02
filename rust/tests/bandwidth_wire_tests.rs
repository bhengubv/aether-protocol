// SPDX-License-Identifier: MIT
//! Integration tests for the ABMF WIRE bindings: BandwidthProbe(53),
//! BandwidthAck(54), BandwidthGossip(55).
//!
//! Mirrors the C# `BandwidthWireTests` behaviour cases: directed probe/ack sends,
//! gossip broadcast + inbound handle raising an event with the source peer,
//! probe/ack handle events, and wrong-type → false. Byte-identity gates live
//! in-lib (`src/bandwidth_wire/service.rs`) against `fixtures/bandwidth/vectors.json`.

#[path = "common.rs"]
mod common;

use std::sync::Arc;

use aethernet_protocol::{
    bandwidth::BandwidthConfidence,
    bandwidth_wire::{BandwidthProbe, BandwidthWireCodec, BandwidthWireService},
    bandwidth::{BandwidthGossipPayload, BandwidthProbeAck},
    constants::DEFAULT_TTL,
    models::PeerInfo,
    protocol::{MeshPacket, PacketType},
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (BandwidthWireService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = BandwidthWireService::new(sender.clone());
    (svc, sender)
}

fn new_svc() -> (BandwidthWireService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL)
}

// ─── Directed sends ─────────────────────────────────────────────

#[tokio::test]
async fn send_probe_emits_directed_probe() {
    let (svc, sender) = new_svc_for("aether:a:01");
    let probe = BandwidthProbe {
        sequence: 42,
        sender_send_us: 1_700_000_000_000_000,
    };
    assert!(svc.send_probe("aether:b:02", &probe).await);

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1, "expected exactly one directed send");
    let sent = &unicasts[0];
    assert_eq!(sent.packet.packet_type, PacketType::BandwidthProbe);
    assert_eq!(sent.next_hop_uhid, "aether:b:02");
    assert_eq!(sent.packet.destination_uhid, "aether:b:02");
    assert_eq!(sent.packet.source_uhid, "aether:a:01");
    assert_eq!(sent.packet.ttl, DEFAULT_TTL);
}

#[tokio::test]
async fn send_ack_emits_directed_ack() {
    let (svc, sender) = new_svc();
    let ack = BandwidthProbeAck {
        sequence: 1,
        sender_send_us: 2,
        receiver_receive_us: 3,
        receiver_send_us: 4,
        sender_receive_us: 5,
        probe_bytes: 6,
    };
    assert!(svc.send_ack("aether:b:02", &ack).await);

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::BandwidthAck);
    assert_eq!(unicasts[0].next_hop_uhid, "aether:b:02");
}

// ─── Gossip broadcast + handle ──────────────────────────────────

#[tokio::test]
async fn broadcast_gossip_emits_gossip_and_handle_raises_event_with_source_peer() {
    let (svc, sender) = new_svc();
    // FakeMeshSender.broadcast returns the connected-peer count; add peers so the
    // fan-out count is non-zero (mirrors the C# fake returning 3).
    sender.add_peer(PeerInfo::new("aether:peer:aa".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:bb".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:cc".into(), Vec::new()));

    let g = BandwidthGossipPayload {
        peer_uhid: String::new(),
        transport_name: String::new(),
        btl_bw_bps: 5_000_000,
        rt_prop_us: 25_000,
        confidence: BandwidthConfidence::Medium,
        measured_at: std::time::SystemTime::UNIX_EPOCH,
    };
    assert_eq!(svc.broadcast_gossip(&g).await, 3);

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1, "expected exactly one broadcast");
    let mut sent = bcasts[0].clone();
    assert_eq!(sent.packet_type, PacketType::BandwidthGossip);
    assert_eq!(sent.destination_uhid, "*");

    // Handle the packet back in with a known source; the event carries it as peer_uhid.
    let mut events = svc.subscribe_gossip();
    sent.source_uhid = "aether:peer:09".to_string();
    assert!(svc.handle(&sent).await);

    let got = events.try_recv().expect("expected a gossip event");
    assert_eq!(got.btl_bw_bps, 5_000_000);
    assert_eq!(got.rt_prop_us, 25_000);
    assert_eq!(got.confidence, BandwidthConfidence::Medium);
    assert_eq!(got.peer_uhid, "aether:peer:09");
}

// ─── Handle probe / ack ─────────────────────────────────────────

#[tokio::test]
async fn handle_probe_raises_probe_received_with_source() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_probe();

    let mut pkt = MeshPacket::new(PacketType::BandwidthProbe, "aether:x:01".to_string());
    pkt.payload = BandwidthWireCodec::serialize_probe(&BandwidthProbe {
        sequence: 9,
        sender_send_us: 123,
    });
    assert!(svc.handle(&pkt).await);

    let got = events.try_recv().expect("expected a probe event");
    assert_eq!(got.probe.sequence, 9);
    assert_eq!(got.probe.sender_send_us, 123);
    assert_eq!(got.from_uhid, "aether:x:01");
}

#[tokio::test]
async fn handle_ack_raises_ack_received() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_ack();

    let mut pkt = MeshPacket::new(PacketType::BandwidthAck, "aether:x:01".to_string());
    pkt.payload = BandwidthWireCodec::serialize_ack(&BandwidthProbeAck {
        sequence: 3,
        sender_send_us: 10,
        receiver_receive_us: 20,
        receiver_send_us: 30,
        sender_receive_us: 0,
        probe_bytes: 64,
    });
    assert!(svc.handle(&pkt).await);

    let got = events.try_recv().expect("expected an ack event");
    assert_eq!(got.sequence, 3);
    assert_eq!(got.probe_bytes, 64);
    assert_eq!(got.sender_receive_us, 0); // not on wire
}

// ─── Rejections ─────────────────────────────────────────────────

#[tokio::test]
async fn handle_wrong_type_returns_false() {
    let (svc, _) = new_svc();
    let mut probe_events = svc.subscribe_probe();
    let mut ack_events = svc.subscribe_ack();
    let mut gossip_events = svc.subscribe_gossip();

    let mut pkt = MeshPacket::new(PacketType::Data, "aether:x:01".to_string());
    pkt.payload = Vec::new();

    assert!(!svc.handle(&pkt).await);
    assert!(probe_events.try_recv().is_err(), "wrong type must not surface a probe");
    assert!(ack_events.try_recv().is_err(), "wrong type must not surface an ack");
    assert!(gossip_events.try_recv().is_err(), "wrong type must not surface a gossip");
}

#[tokio::test]
async fn handle_short_body_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_probe();

    // Correct type, but the body is one byte short of the 12-byte probe layout.
    let mut pkt = MeshPacket::new(PacketType::BandwidthProbe, "aether:x:01".to_string());
    pkt.payload = vec![0u8; 11];

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "short body must not surface");
}
