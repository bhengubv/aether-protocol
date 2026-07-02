// SPDX-License-Identifier: MIT
//! Integration tests for the SpaceBreadcrumb WIRE service (PacketType 40).
//!
//! Mirrors the C# `WirePacketsTests` Space cases: broadcast emits a breadcrumb
//! packet + handle raises a received event, and a wrong packet type returns false.

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;

use aethernet_protocol::{
    constants::DEFAULT_TTL,
    protocol::{MeshPacket, PacketType},
    space_wire::SpaceBreadcrumbService,
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (SpaceBreadcrumbService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = SpaceBreadcrumbService::new(sender.clone());
    (svc, sender)
}

fn new_svc() -> (SpaceBreadcrumbService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL)
}

/// Build an inbound SpaceBreadcrumb packet from `source`. Built independently of
/// the service's private wire struct (via serde_json) so the test pins the wire
/// shape, mirroring the C# test helper. `signature` is a byte slice base64-encoded
/// on the wire (STANDARD).
fn breadcrumb_packet(
    content_hash: &str,
    geo_hash: &str,
    anchor_uhid: &str,
    created_at_ms: i64,
    ttl_hours: i32,
    crumb_type: i32,
    signature: &[u8],
    source: &str,
) -> MeshPacket {
    use base64::{engine::general_purpose::STANDARD, Engine as _};
    let body = serde_json::to_vec(&json!({
        "content_hash": content_hash,
        "geo_hash": geo_hash,
        "anchor_uhid": anchor_uhid,
        "created_at_ms": created_at_ms,
        "ttl_hours": ttl_hours,
        "type": crumb_type,
        "signature": STANDARD.encode(signature),
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::SpaceBreadcrumb, source.to_string());
    p.destination_uhid = "*".to_string();
    p.payload = body;
    p
}

// ─── Broadcast + Handle ─────────────────────────────────

#[tokio::test]
async fn broadcast_emits_breadcrumb_packet_and_handle_raises_event() {
    let (svc, sender) = new_svc_for("aether:alice:01");
    // Two connected peers -> FakeMeshSender.broadcast returns 2 (mirrors the C# fake).
    sender.add_peer(aethernet_protocol::models::PeerInfo::new(
        "aether:peer:aa".into(),
        Vec::new(),
    ));
    sender.add_peer(aethernet_protocol::models::PeerInfo::new(
        "aether:peer:bb".into(),
        Vec::new(),
    ));

    let signature = vec![0x99u8; 64];
    let reached = svc
        .broadcast(
            "QmX",
            "u4pruy",
            "aether:alice:01",
            1_700_000_000_000,
            720,
            1, // Emergency
            &signature,
        )
        .await;
    assert_eq!(reached, 2);

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1, "expected exactly one broadcast");
    let sent = &bcasts[0];
    assert_eq!(sent.packet_type, PacketType::SpaceBreadcrumb);
    assert_eq!(sent.destination_uhid, "*");
    assert_eq!(sent.ttl, DEFAULT_TTL);
    assert_eq!(sent.source_uhid, "aether:alice:01");

    // Feed the broadcast back into handle — must surface a received event.
    let mut events = svc.subscribe_received();
    let ok = svc.handle(sent).await;
    assert!(ok);

    let got = events.try_recv().expect("expected a breadcrumb-received event");
    assert_eq!(got.geo_hash, "u4pruy");
    assert_eq!(got.crumb_type, 1);
    assert_eq!(got.ttl_hours, 720);
    assert_eq!(got.signature.len(), 64);
    assert_eq!(got.content_hash, "QmX");
    assert_eq!(got.anchor_uhid, "aether:alice:01");
    assert_eq!(got.created_at_ms, 1_700_000_000_000);
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

    let mut pkt = breadcrumb_packet(
        "QmX",
        "u4pruy",
        "aether:alice:01",
        1,
        72,
        0,
        &[],
        "aether:bob:02",
    );
    pkt.payload = b"not json".to_vec();

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "malformed payload must not surface");
}

#[tokio::test]
async fn handle_empty_content_hash_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_received();

    let pkt = breadcrumb_packet("", "u4pruy", "aether:alice:01", 1, 72, 0, &[], "aether:bob:02");

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "empty content hash must not surface");
}

#[tokio::test]
async fn handle_unsigned_breadcrumb_round_trips_empty_signature() {
    // notice_unsigned shape: empty signature ("" on the wire) decodes to an empty Vec.
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_received();

    let pkt = breadcrumb_packet(
        "QmNotice777",
        "gcpvj0",
        "aether:bob:02",
        0,
        72,
        0, // Notice
        &[],
        "aether:bob:02",
    );

    assert!(svc.handle(&pkt).await);
    let got = events.try_recv().expect("expected a breadcrumb-received event");
    assert_eq!(got.content_hash, "QmNotice777");
    assert_eq!(got.crumb_type, 0);
    assert!(got.signature.is_empty(), "unsigned breadcrumb has an empty signature");
}
