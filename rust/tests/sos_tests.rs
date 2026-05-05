// SPDX-License-Identifier: MIT
//! Integration tests for the SOS service.

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;
use uuid::Uuid;

use aether_protocol::{
    constants::{MAX_SOS_BROADCASTS_PER_HOUR, SOS_PRIORITY, SOS_TTL},
    extensibility::{NoopBackendClient, NoopIncentiveProvider},
    protocol::{MeshPacket, PacketType},
    sos::SosBroadcastService,
};
use common::FakeMeshSender;

const LOCAL: &str = "local";

async fn new_svc() -> (SosBroadcastService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(LOCAL);
    let svc = SosBroadcastService::with_dependencies(
        sender.clone(),
        Arc::new(NoopBackendClient),
        Arc::new(NoopIncentiveProvider),
    );
    (svc, sender)
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
