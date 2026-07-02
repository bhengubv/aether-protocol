// SPDX-License-Identifier: MIT
//! Integration tests for the SOS service.

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;
use uuid::Uuid;

use aethernet_protocol::{
    constants::{MAX_SOS_BROADCASTS_PER_HOUR, SOS_PRIORITY, SOS_TTL},
    extensibility::{NoopBackendClient, NoopIncentiveProvider},
    protocol::{MeshPacket, PacketType},
    sos::SosBroadcastService,
};
use common::FakeMeshSender;

const LOCAL: &str = "local";

async fn new_svc() -> (SosBroadcastService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL).await
}

async fn new_svc_for(local: &str) -> (SosBroadcastService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = SosBroadcastService::with_dependencies(
        sender.clone(),
        Arc::new(NoopBackendClient),
        Arc::new(NoopIncentiveProvider),
    );
    (svc, sender)
}

/// Originate a real SosBroadcast packet on a separate node and return it + its id.
async fn originate_sos(origin_uhid: &str) -> (MeshPacket, Uuid) {
    let (origin, origin_sender) = new_svc_for(origin_uhid).await;
    origin
        .broadcast("medical", Some("help".into()), -26.20, 28.04, Some("ke7g".into()))
        .await;
    let sos = origin_sender.broadcasts()[0].clone();
    let id = origin.get_active_alerts()[0].id;
    (sos, id)
}

/// Build a directed SosAck packet from `responder` acknowledging `broadcast_id`.
fn make_ack(broadcast_id: Uuid, responder: &str) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "broadcast_id": broadcast_id,
        "received_at_ms": 1_700_000_000_000i64,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::SosAck, responder.to_string());
    p.destination_uhid = "aether:origin:aa".to_string();
    p.priority = SOS_PRIORITY;
    p.payload = body;
    p
}

fn new_sos_packet(source: &str, ttl: i32) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "broadcast_id": Uuid::new_v4(),
        "broadcast_type": "sos",
        "message": "help",
        "latitude": -33.9,
        "longitude": 18.4,
        "geohash": null,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::SosBroadcast, source.to_string());
    p.destination_uhid = String::new();
    p.ttl = ttl;
    p.priority = SOS_PRIORITY;
    p.payload = body;
    p
}

// ─── Broadcast ──────────────────────────────────────────

#[tokio::test]
async fn broadcast_floods_and_stores_alert() {
    let (svc, sender) = new_svc().await;
    let ok = svc
        .broadcast("sos", Some("help".to_string()), -33.9, 18.4, None)
        .await;
    assert!(ok);
    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1);
    assert_eq!(bcasts[0].packet_type, PacketType::SosBroadcast);
    assert_eq!(bcasts[0].ttl, SOS_TTL);
    assert_eq!(bcasts[0].priority, SOS_PRIORITY);
    assert_eq!(svc.get_active_alerts().len(), 1);
}

#[tokio::test]
async fn broadcast_rate_limited_after_max() {
    let (svc, _) = new_svc().await;
    for _ in 0..MAX_SOS_BROADCASTS_PER_HOUR {
        assert!(svc.broadcast("sos", Some("h".into()), 0.0, 0.0, None).await);
    }
    assert!(!svc.broadcast("sos", Some("h".into()), 0.0, 0.0, None).await);
}

#[tokio::test]
async fn broadcast_rejects_empty_type() {
    let (svc, _) = new_svc().await;
    let ok = svc.broadcast("", Some("help".into()), 0.0, 0.0, None).await;
    assert!(!ok);
}

// ─── Handle ─────────────────────────────────────────────

#[tokio::test]
async fn handle_drops_duplicate_packet_id() {
    let (svc, sender) = new_svc().await;
    let mut pkt = new_sos_packet("alice", SOS_TTL);
    let pkt_id = pkt.id;

    svc.handle(&mut pkt).await;
    sender.clear();
    let alerts_after = svc.get_active_alerts().len();

    let mut pkt2 = new_sos_packet("alice", SOS_TTL);
    pkt2.id = pkt_id; // re-use id
    svc.handle(&mut pkt2).await;

    assert!(sender.broadcasts().is_empty());
    assert_eq!(svc.get_active_alerts().len(), alerts_after);
}

#[tokio::test]
async fn handle_ignores_self_originated() {
    let (svc, sender) = new_svc().await;
    let mut pkt = new_sos_packet(LOCAL, SOS_TTL);
    svc.handle(&mut pkt).await;
    assert!(sender.broadcasts().is_empty());
}

#[tokio::test]
async fn handle_rebroadcasts_when_ttl_allows() {
    let (svc, sender) = new_svc().await;
    let mut pkt = new_sos_packet("alice", 5);
    svc.handle(&mut pkt).await;
    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1);
    assert_eq!(bcasts[0].ttl, 4);
}

#[tokio::test]
async fn handle_does_not_rebroadcast_when_ttl_exhausted() {
    let (svc, sender) = new_svc().await;
    let mut pkt = new_sos_packet("alice", 1);
    svc.handle(&mut pkt).await;
    assert!(sender.broadcasts().is_empty());
}

// ─── Resolve ────────────────────────────────────────────

#[tokio::test]
async fn resolve_removes_alert() {
    let (svc, _) = new_svc().await;
    svc.broadcast("sos", Some("h".into()), 0.0, 0.0, None).await;
    let alerts = svc.get_active_alerts();
    assert_eq!(alerts.len(), 1);
    let id = alerts[0].id;
    svc.resolve(&id);
    assert!(svc.get_active_alerts().is_empty());
}

// ─── SosAck: receiver side ──────────────────────────────

#[tokio::test]
async fn handle_receiving_sos_sends_directed_ack_to_originator() {
    let (sos, id) = originate_sos("aether:origin:aa").await;

    let (receiver, receiver_sender) = new_svc_for("aether:receiver:bb").await;
    let mut pkt = sos.clone();
    receiver.handle(&mut pkt).await;

    let sends = receiver_sender.unicasts();
    assert_eq!(sends.len(), 1, "expected exactly one directed ack");
    let ack = &sends[0];
    assert_eq!(ack.packet.packet_type, PacketType::SosAck);
    assert_eq!(ack.next_hop_uhid, "aether:origin:aa");
    assert_eq!(ack.packet.destination_uhid, "aether:origin:aa");

    // Payload broadcast_id must match the SOS we acknowledged.
    let body: serde_json::Value = serde_json::from_slice(&ack.packet.payload).unwrap();
    assert_eq!(body["broadcast_id"], json!(id.to_string()));
}

#[tokio::test]
async fn handle_own_sos_does_not_ack() {
    // A node that originated an SOS must not ack its own re-observed broadcast.
    let (svc, sender) = new_svc_for("aether:origin:aa").await;
    svc.broadcast("panic", None, 0.0, 0.0, None).await;
    let mut own = sender.broadcasts()[0].clone();

    svc.handle(&mut own).await;
    assert!(sender.unicasts().is_empty(), "own SOS must not generate an ack");
}

// ─── SosAck: originator side ────────────────────────────

#[tokio::test]
async fn handle_ack_on_originator_records_responder_and_emits_event() {
    let (origin, _) = new_svc_for("aether:origin:aa").await;
    origin.broadcast("fire", Some("north wing".into()), -26.1, 28.0, None).await;
    let id = origin.get_active_alerts()[0].id;

    let mut events = origin.subscribe_acknowledged();
    origin.handle_ack(&make_ack(id, "aether:responder:cc")).await.unwrap();

    let evt = events.try_recv().expect("expected an sos_acknowledged event");
    assert_eq!(evt.broadcast_id, id);
    assert_eq!(evt.responder_uhid, "aether:responder:cc");
    assert_eq!(evt.total_distinct_acks, 1);
    assert!(origin
        .acknowledged_by(&id)
        .contains(&"aether:responder:cc".to_string()));
}

#[tokio::test]
async fn handle_ack_duplicate_responder_counted_once() {
    let (origin, _) = new_svc_for("aether:origin:aa").await;
    origin.broadcast("medical", None, 0.0, 0.0, None).await;
    let id = origin.get_active_alerts()[0].id;

    let mut events = origin.subscribe_acknowledged();
    origin.handle_ack(&make_ack(id, "aether:responder:cc")).await.unwrap();
    origin.handle_ack(&make_ack(id, "aether:responder:cc")).await.unwrap(); // same responder again

    assert!(events.try_recv().is_ok(), "first distinct ack emits an event");
    assert!(
        events.try_recv().is_err(),
        "duplicate responder must not emit a second event"
    );
    assert_eq!(origin.acknowledged_by(&id).len(), 1);
}

#[tokio::test]
async fn handle_ack_two_distinct_responders_counts_two() {
    let (origin, _) = new_svc_for("aether:origin:aa").await;
    origin.broadcast("medical", None, 0.0, 0.0, None).await;
    let id = origin.get_active_alerts()[0].id;

    origin.handle_ack(&make_ack(id, "aether:responder:cc")).await.unwrap();
    origin.handle_ack(&make_ack(id, "aether:responder:dd")).await.unwrap();

    assert_eq!(origin.acknowledged_by(&id).len(), 2);
}

#[tokio::test]
async fn handle_ack_unknown_broadcast_is_noop() {
    let (svc, _) = new_svc_for("aether:local:01").await;
    let mut events = svc.subscribe_acknowledged();

    svc.handle_ack(&make_ack(Uuid::new_v4(), "aether:responder:cc"))
        .await
        .unwrap();

    assert!(
        events.try_recv().is_err(),
        "ack for an SOS this node did not originate must be a no-op"
    );
}

#[tokio::test]
async fn handle_ack_ignores_self_responder() {
    // An ack whose source is the originating node itself (echo) must be ignored.
    let (origin, _) = new_svc_for("aether:origin:aa").await;
    origin.broadcast("medical", None, 0.0, 0.0, None).await;
    let id = origin.get_active_alerts()[0].id;

    origin.handle_ack(&make_ack(id, "aether:origin:aa")).await.unwrap();
    assert!(origin.acknowledged_by(&id).is_empty());
}

#[tokio::test]
async fn handle_ack_wrong_packet_type_errors() {
    let (svc, _) = new_svc_for("aether:origin:aa").await;
    let mut pkt = make_ack(Uuid::new_v4(), "aether:responder:cc");
    pkt.packet_type = PacketType::Data;
    assert!(svc.handle_ack(&pkt).await.is_err());
}
