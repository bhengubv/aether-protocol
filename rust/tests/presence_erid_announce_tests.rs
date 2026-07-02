// SPDX-License-Identifier: MIT
//! Integration tests for PresenceBeacon(21)/PresenceQuery(22)
//! ([`PresenceService`]) and the EridAnnounce(56) mesh binding
//! ([`EridAnnounceService`]).
//!
//! Mirrors the C# `PresenceEridAnnounceTests` behaviour cases: beacon/query
//! broadcast + inbound handle raising an event with the source peer, wrong-type →
//! false, empty-erid beacon → false, directed ERID-announce send + inbound handle,
//! and wrong-type / empty-body → false. The presence byte-identity gates and the
//! ERID-announcement frame re-pin (against `fixtures/erid`) live in-lib
//! (`src/presence/service.rs`, `src/erid_announce/service.rs`).

#[path = "common.rs"]
mod common;

use std::sync::Arc;

use aethernet_protocol::{
    erid_announce::EridAnnounceService,
    models::PeerInfo,
    presence::{PresenceBeaconPayload, PresenceService},
    protocol::{MeshPacket, PacketType},
};
use uuid::Uuid;

use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_presence(local: &str) -> (PresenceService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = PresenceService::new(sender.clone());
    (svc, sender)
}

fn new_erid_announce(local: &str) -> (EridAnnounceService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = EridAnnounceService::new(sender.clone());
    (svc, sender)
}

/// Add four peers so `FakeMeshSender.broadcast` reports a fan-out of 4, mirroring
/// the C# fake's hardcoded `BroadcastAsync` return of 4.
fn add_four_peers(sender: &Arc<FakeMeshSender>) {
    sender.add_peer(PeerInfo::new("aether:peer:aa".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:bb".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:cc".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:dd".into(), Vec::new()));
}

// ─── Presence behaviour ─────────────────────────────────────────────────────

#[tokio::test]
async fn broadcast_beacon_emits_beacon_packet_and_handle_raises_event() {
    let (svc, sender) = new_presence("aether:alice:01");
    add_four_peers(&sender);
    let beacon = PresenceBeaconPayload {
        erid: "3B38HPPFG9JXE37Q".to_string(),
        geohash: "u4pru".to_string(),
        capabilities: 73,
        status: 1,
        sent_at_ms: 1_700_000_000_000,
    };

    assert_eq!(svc.broadcast_beacon(beacon).await, 4);

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1, "expected exactly one broadcast");
    let mut sent = bcasts[0].clone();
    assert_eq!(sent.packet_type, PacketType::PresenceBeacon);
    assert_eq!(sent.destination_uhid, "*");

    // Handle the packet back in with a known source; the event carries it.
    let mut events = svc.subscribe_beacon_received();
    sent.source_uhid = "aether:alice:01".to_string();
    assert!(svc.handle(&sent).await);

    let got = events.try_recv().expect("expected a beacon-received event");
    assert_eq!(got.beacon.erid, "3B38HPPFG9JXE37Q");
    assert_eq!(got.from_uhid, "aether:alice:01");
}

#[tokio::test]
async fn query_emits_query_packet_and_handle_raises_event() {
    let (svc, sender) = new_presence("aether:bob:02");

    let qid = svc.query("u4pru").await;
    assert_ne!(qid, Uuid::nil());

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1, "expected exactly one broadcast");
    let mut sent = bcasts[0].clone();
    assert_eq!(sent.packet_type, PacketType::PresenceQuery);

    let body: aethernet_protocol::presence::PresenceQueryPayload =
        serde_json::from_slice(&sent.payload).expect("parse presence query");
    assert_eq!(body.query_id, qid);
    assert_eq!(body.geohash, "u4pru");

    let mut events = svc.subscribe_query_received();
    sent.source_uhid = "aether:bob:02".to_string();
    assert!(svc.handle(&sent).await);

    let got = events.try_recv().expect("expected a query-received event");
    assert_eq!(got.query.query_id, qid);
}

#[tokio::test]
async fn presence_handle_wrong_type_returns_false() {
    let (svc, _) = new_presence(LOCAL);
    let pkt = MeshPacket::new(PacketType::Data, "aether:x:01".to_string());
    assert!(!svc.handle(&pkt).await);
}

#[tokio::test]
async fn presence_handle_beacon_with_empty_erid_returns_false() {
    let (svc, _) = new_presence(LOCAL);
    let beacon = PresenceBeaconPayload {
        erid: String::new(),
        geohash: String::new(),
        capabilities: 0,
        status: 0,
        sent_at_ms: 0,
    };
    let mut pkt = MeshPacket::new(PacketType::PresenceBeacon, "aether:x:01".to_string());
    pkt.payload = serde_json::to_vec(&beacon).unwrap();
    assert!(!svc.handle(&pkt).await);
}

// ─── EridAnnounce(56) transport ─────────────────────────────────────────────

#[tokio::test]
async fn erid_announce_send_emits_directed_packet_and_handle_raises_event() {
    let (svc, sender) = new_erid_announce("aether:alice:01");
    let enc = [1u8, 2, 3, 4, 5]; // opaque Signal-encrypted announcement

    assert!(svc.send_announce("aether:bob:02", &enc).await);

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1, "expected exactly one directed send");
    let mut sent = unicasts[0].clone();
    assert_eq!(sent.packet.packet_type, PacketType::EridAnnounce);
    assert_eq!(sent.next_hop_uhid, "aether:bob:02");
    assert_eq!(sent.packet.destination_uhid, "aether:bob:02");

    let mut events = svc.subscribe_announce_received();
    sent.packet.source_uhid = "aether:bob:02".to_string();
    assert!(svc.handle(&sent.packet).await);

    let got = events.try_recv().expect("expected an announce-received event");
    assert_eq!(got.encrypted_announcement, enc.to_vec());
    assert_eq!(got.from_uhid, "aether:bob:02");
}

#[tokio::test]
async fn erid_announce_handle_wrong_type_or_empty_returns_false() {
    let (svc, _) = new_erid_announce(LOCAL);

    // Wrong type.
    let mut wrong = MeshPacket::new(PacketType::Data, "aether:x:01".to_string());
    wrong.payload = vec![1];
    assert!(!svc.handle(&wrong).await);

    // Right type, empty body.
    let empty = MeshPacket::new(PacketType::EridAnnounce, "aether:x:01".to_string());
    assert!(!svc.handle(&empty).await);
}

// ─── EridAnnounce send guards ───────────────────────────────────────────────

#[tokio::test]
async fn erid_announce_send_rejects_empty_peer_or_body() {
    let (svc, sender) = new_erid_announce(LOCAL);

    assert!(!svc.send_announce("", &[1, 2, 3]).await, "empty peer must not send");
    assert!(!svc.send_announce("aether:bob:02", &[]).await, "empty body must not send");
    assert_eq!(sender.unicasts().len(), 0, "no packet should be sent");
}
