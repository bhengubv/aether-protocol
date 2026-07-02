// SPDX-License-Identifier: MIT
//! Integration tests for the ForgeAnnounce WIRE service (PacketType 41).
//!
//! Mirrors the C# `WirePacketsTests` Forge cases: broadcast emits an announce
//! packet + handle raises a received event, and a wrong packet type returns false.

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;

use aethernet_protocol::{
    constants::DEFAULT_TTL,
    forge_wire::ForgeAnnounceService,
    models::PeerInfo,
    protocol::{MeshPacket, PacketType},
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (ForgeAnnounceService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = ForgeAnnounceService::new(sender.clone());
    (svc, sender)
}

fn new_svc() -> (ForgeAnnounceService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL)
}

/// Build an inbound ForgeAnnounce packet from `source`. Built independently of the
/// service's private wire struct (via serde_json) so the test pins the wire shape.
fn announce_packet(
    package_id: &str,
    content_hash: &str,
    size_bytes: i64,
    announced_at_ms: i64,
    source: &str,
) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "package_id": package_id,
        "content_hash": content_hash,
        "size_bytes": size_bytes,
        "announced_at_ms": announced_at_ms,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::ForgeAnnounce, source.to_string());
    p.destination_uhid = "*".to_string();
    p.payload = body;
    p
}

// ─── Broadcast + Handle ─────────────────────────────────

#[tokio::test]
async fn broadcast_emits_announce_packet_and_handle_raises_event() {
    let (svc, sender) = new_svc_for("aether:alice:01");
    sender.add_peer(PeerInfo::new("aether:peer:aa".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:bb".into(), Vec::new()));

    let reached = svc
        .broadcast("npm:react@18.2.0", "QmForgeHash456", 294912, 1_700_000_000_000)
        .await;
    assert_eq!(reached, 2);

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1, "expected exactly one broadcast");
    let sent = &bcasts[0];
    assert_eq!(sent.packet_type, PacketType::ForgeAnnounce);
    assert_eq!(sent.destination_uhid, "*");
    assert_eq!(sent.ttl, DEFAULT_TTL);
    assert_eq!(sent.source_uhid, "aether:alice:01");

    let mut events = svc.subscribe_received();
    assert!(svc.handle(sent).await);

    let got = events.try_recv().expect("expected an announce-received event");
    assert_eq!(got.package_id, "npm:react@18.2.0");
    assert_eq!(got.size_bytes, 294912);
    assert_eq!(got.content_hash, "QmForgeHash456");
    assert_eq!(got.announced_at_ms, 1_700_000_000_000);
}

#[tokio::test]
async fn handle_wrong_packet_type_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_received();

    let mut pkt = MeshPacket::new(PacketType::Data, "aether:bob:02".to_string());
    pkt.payload = Vec::new();

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "wrong type must not surface");
}

#[tokio::test]
async fn handle_malformed_payload_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_received();

    let mut pkt = announce_packet("npm:react@18.2.0", "QmForgeHash456", 1, 1, "aether:bob:02");
    pkt.payload = b"not json".to_vec();

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "malformed payload must not surface");
}

#[tokio::test]
async fn handle_empty_package_id_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_received();

    let pkt = announce_packet("", "QmForgeHash456", 1, 1, "aether:bob:02");

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "empty package id must not surface");
}
