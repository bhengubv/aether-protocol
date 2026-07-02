// SPDX-License-Identifier: MIT
//! Integration tests for the Heartbeat service (PacketType 10).

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;

use aethernet_protocol::{
    heartbeat::HeartbeatService,
    models::PeerInfo,
    protocol::{MeshPacket, PacketType},
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (HeartbeatService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = HeartbeatService::new(sender.clone());
    (svc, sender)
}

fn new_svc() -> (HeartbeatService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL)
}

/// Build an inbound Heartbeat packet from `source` with the given sequence /
/// timestamp. Built independently of the service's private wire struct so the
/// test pins the wire shape.
fn heartbeat_from(source: &str, sequence: i32, sent_at_ms: i64) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "sequence": sequence,
        "sent_at_ms": sent_at_ms,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::Heartbeat, source.to_string());
    p.destination_uhid = "*".to_string();
    p.payload = body;
    p
}

// ─── Send ───────────────────────────────────────────────

#[tokio::test]
async fn send_broadcasts_heartbeat_with_incrementing_sequence() {
    let (svc, sender) = new_svc();

    svc.send_heartbeat().await;
    svc.send_heartbeat().await;

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 2);
    for p in &bcasts {
        assert_eq!(p.packet_type, PacketType::Heartbeat);
        assert_eq!(p.ttl, 1);
        assert_eq!(p.source_uhid, LOCAL);
        assert_eq!(p.destination_uhid, "*");
    }

    let first: serde_json::Value = serde_json::from_slice(&bcasts[0].payload).unwrap();
    let second: serde_json::Value = serde_json::from_slice(&bcasts[1].payload).unwrap();
    assert_eq!(first["sequence"], json!(1));
    assert_eq!(second["sequence"], json!(2));
}

#[tokio::test]
async fn send_returns_delivered_peer_count() {
    let (svc, sender) = new_svc();
    // FakeMeshSender.broadcast returns the connected-peer count.
    sender.add_peer(PeerInfo::new("aether:peer:aa".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:bb".into(), Vec::new()));

    let delivered = svc.send_heartbeat().await;
    assert_eq!(delivered, 2);
}

// ─── Handle ─────────────────────────────────────────────

#[tokio::test]
async fn handle_records_peer_and_emits_event() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_peer_seen();

    let ok = svc
        .handle(&heartbeat_from("aether:peer:aa", 7, 1_700_000_000_000))
        .await;

    assert!(ok);
    let seen = events.try_recv().expect("expected a peer_seen event");
    assert_eq!(seen.uhid, "aether:peer:aa");
    assert_eq!(seen.last_sequence, 7);
    assert_eq!(seen.last_sent_at_ms, 1_700_000_000_000);

    let known = svc.get_known_peers();
    assert_eq!(known.len(), 1);
    assert_eq!(known[0].uhid, "aether:peer:aa");
}

#[tokio::test]
async fn handle_refreshes_existing_peer() {
    let (svc, _) = new_svc();
    svc.handle(&heartbeat_from("aether:peer:aa", 1, 1000)).await;
    svc.handle(&heartbeat_from("aether:peer:aa", 2, 2000)).await;

    let known = svc.get_known_peers();
    assert_eq!(known.len(), 1, "same peer must not create a second record");
    assert_eq!(known[0].last_sequence, 2);
    assert_eq!(known[0].last_sent_at_ms, 2000);
}

#[tokio::test]
async fn handle_own_heartbeat_is_ignored() {
    let (svc, _) = new_svc();
    let ok = svc.handle(&heartbeat_from(LOCAL, 1, 1000)).await;
    assert!(!ok);
    assert!(svc.get_known_peers().is_empty());
}

#[tokio::test]
async fn handle_wrong_packet_type_returns_false() {
    let (svc, _) = new_svc();
    let mut pkt = heartbeat_from("aether:peer:aa", 1, 1000);
    pkt.packet_type = PacketType::Data;
    assert!(!svc.handle(&pkt).await);
    assert!(svc.get_known_peers().is_empty());
}

#[tokio::test]
async fn handle_malformed_payload_returns_false() {
    let (svc, _) = new_svc();
    let mut pkt = heartbeat_from("aether:peer:aa", 1, 1000);
    pkt.payload = b"not json".to_vec();
    assert!(!svc.handle(&pkt).await);
    assert!(svc.get_known_peers().is_empty());
}

// ─── Live peers ─────────────────────────────────────────

#[tokio::test]
async fn get_live_peers_includes_recently_seen_peer() {
    let (svc, _) = new_svc();
    svc.handle(&heartbeat_from("aether:peer:aa", 1, 1000)).await;

    // A just-received heartbeat is live within any generous window.
    let live = svc.get_live_peers(3600);
    assert_eq!(live.len(), 1);
    assert_eq!(live[0].uhid, "aether:peer:aa");

    // A negative window pushes the recency horizon into the future, so it
    // excludes even a just-seen peer — a deterministic proof the filter filters
    // (no wall-clock race).
    assert!(svc.get_live_peers(-1).is_empty());
}
