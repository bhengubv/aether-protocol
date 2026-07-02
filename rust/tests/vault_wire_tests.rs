// SPDX-License-Identifier: MIT
//! Integration tests for the VaultShardRequest WIRE service (PacketType 42).
//!
//! Mirrors the C# `WirePacketsTests` Vault cases: request_shard emits a
//! shard-request packet (requester = sender's local UHID) + handle raises a
//! received event, and a wrong packet type returns false.

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;

use aethernet_protocol::{
    constants::DEFAULT_TTL,
    models::PeerInfo,
    protocol::{MeshPacket, PacketType},
    vault_wire::VaultShardRequestService,
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (VaultShardRequestService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = VaultShardRequestService::new(sender.clone());
    (svc, sender)
}

fn new_svc() -> (VaultShardRequestService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL)
}

/// Build an inbound VaultShardRequest packet from `source`. Built independently of
/// the service's private wire struct (via serde_json) so the test pins the wire shape.
fn shard_request_packet(shard_hash: &str, requester_uhid: &str, source: &str) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "shard_hash": shard_hash,
        "requester_uhid": requester_uhid,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::VaultShardRequest, source.to_string());
    p.destination_uhid = "*".to_string();
    p.payload = body;
    p
}

// ─── Request + Handle ───────────────────────────────────

#[tokio::test]
async fn request_shard_emits_packet_and_handle_raises_event() {
    let (svc, sender) = new_svc_for("aether:bob:02");
    sender.add_peer(PeerInfo::new("aether:peer:aa".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:bb".into(), Vec::new()));

    let reached = svc.request_shard("QmShardHash789").await;
    assert_eq!(reached, 2);

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1, "expected exactly one broadcast");
    let sent = &bcasts[0];
    assert_eq!(sent.packet_type, PacketType::VaultShardRequest);
    assert_eq!(sent.destination_uhid, "*");
    assert_eq!(sent.ttl, DEFAULT_TTL);
    assert_eq!(sent.source_uhid, "aether:bob:02");

    // Requester is the sender's local UHID (mirrors the C# body assertion).
    let body: serde_json::Value = serde_json::from_slice(&sent.payload).unwrap();
    assert_eq!(body["shard_hash"], json!("QmShardHash789"));
    assert_eq!(body["requester_uhid"], json!("aether:bob:02"));

    let mut events = svc.subscribe_requested();
    assert!(svc.handle(sent).await);

    let got = events.try_recv().expect("expected a shard-requested event");
    assert_eq!(got.shard_hash, "QmShardHash789");
    assert_eq!(got.requester_uhid, "aether:bob:02");
}

#[tokio::test]
async fn handle_wrong_packet_type_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_requested();

    let mut pkt = MeshPacket::new(PacketType::Data, "aether:bob:02".to_string());
    pkt.payload = Vec::new();

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "wrong type must not surface");
}

#[tokio::test]
async fn handle_malformed_payload_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_requested();

    let mut pkt = shard_request_packet("QmShardHash789", "aether:bob:02", "aether:bob:02");
    pkt.payload = b"not json".to_vec();

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "malformed payload must not surface");
}

#[tokio::test]
async fn handle_empty_shard_hash_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_requested();

    let pkt = shard_request_packet("", "aether:bob:02", "aether:bob:02");

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "empty shard hash must not surface");
}
