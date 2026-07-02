// SPDX-License-Identifier: MIT
//! Integration tests for the ProfileSync service (PacketType 23).

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;

use aethernet_protocol::{
    constants::DEFAULT_TTL,
    profiles::ProfileService,
    protocol::{MeshPacket, PacketType},
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (ProfileService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = ProfileService::new(sender.clone());
    (svc, sender)
}

fn new_svc() -> (ProfileService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL)
}

/// Build an inbound ProfileSync packet from `uhid`. Built independently of the
/// service's wire struct (via serde_json) so the test pins the wire shape,
/// mirroring the C# `ProfilePacket` helper.
fn profile_packet(
    uhid: &str,
    name: &str,
    avatar: &str,
    status: &str,
    updated_at_ms: i64,
) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "uhid": uhid,
        "display_name": name,
        "avatar_ref": avatar,
        "status_message": status,
        "updated_at_ms": updated_at_ms,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::ProfileSync, uhid.to_string());
    p.destination_uhid = LOCAL.to_string();
    p.payload = body;
    p
}

// ─── Local profile ──────────────────────────────────────

#[test]
fn set_local_profile_stamps_fields() {
    let (svc, _) = new_svc_for("aether:alice:01");
    svc.set_local_profile("Alice", "blake3:abc", "available");

    let local = svc.get_local_profile();
    assert_eq!(local.uhid, "aether:alice:01");
    assert_eq!(local.display_name, "Alice");
    assert_eq!(local.avatar_ref, "blake3:abc");
    assert_eq!(local.status_message, "available");
}

// ─── Publish (directed) ─────────────────────────────────

#[tokio::test]
async fn publish_profile_to_sends_directed_profile_to_peer() {
    let (svc, sender) = new_svc_for("aether:alice:01");
    svc.set_local_profile("Alice", "blake3:abc", "available");

    let ok = svc.publish_profile_to("aether:bob:02").await;
    assert!(ok);

    let sends = sender.unicasts();
    assert_eq!(sends.len(), 1, "expected exactly one directed profile send");
    let sent = &sends[0];
    assert_eq!(sent.packet.packet_type, PacketType::ProfileSync);
    assert_eq!(sent.next_hop_uhid, "aether:bob:02");
    assert_eq!(sent.packet.destination_uhid, "aether:bob:02");
    assert_eq!(sent.packet.ttl, DEFAULT_TTL);

    let body: serde_json::Value = serde_json::from_slice(&sent.packet.payload).unwrap();
    assert_eq!(body["uhid"], json!("aether:alice:01"));
    assert_eq!(body["display_name"], json!("Alice"));
}

#[tokio::test]
async fn publish_profile_to_empty_peer_is_rejected() {
    let (svc, sender) = new_svc_for("aether:alice:01");
    let ok = svc.publish_profile_to("").await;
    assert!(!ok);
    assert!(sender.unicasts().is_empty());
}

// ─── Handle ─────────────────────────────────────────────

#[tokio::test]
async fn handle_caches_peer_profile_and_raises_event() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_profile_updated();

    let ok = svc
        .handle(&profile_packet(
            "aether:bob:02",
            "Bob",
            "blake3:xyz",
            "busy",
            1_700_000_000_000,
        ))
        .await;

    assert!(ok);
    let updated = events.try_recv().expect("expected a profile-updated event");
    assert_eq!(updated.display_name, "Bob");
    assert_eq!(updated.uhid, "aether:bob:02");

    let cached = svc.get_profile("aether:bob:02").expect("profile must be cached");
    assert_eq!(cached.status_message, "busy");
    assert_eq!(svc.get_known_profiles().len(), 1);
}

#[tokio::test]
async fn handle_refreshes_existing_profile() {
    let (svc, _) = new_svc();
    svc.handle(&profile_packet("aether:bob:02", "Bob", "", "here", 1000))
        .await;
    svc.handle(&profile_packet("aether:bob:02", "Bob", "", "away", 2000))
        .await;

    let cached = svc.get_profile("aether:bob:02").unwrap();
    assert_eq!(cached.status_message, "away");
    assert_eq!(
        svc.get_known_profiles().len(),
        1,
        "same peer must not create a second record"
    );
}

#[tokio::test]
async fn handle_own_profile_is_ignored() {
    let (svc, _) = new_svc_for("aether:local:01");
    let ok = svc
        .handle(&profile_packet("aether:local:01", "Me", "", "", 1))
        .await;
    assert!(!ok);
    assert!(svc.get_known_profiles().is_empty());
}

#[tokio::test]
async fn handle_wrong_packet_type_returns_false() {
    let (svc, _) = new_svc();
    let mut pkt = profile_packet("aether:bob:02", "Bob", "", "", 1);
    pkt.packet_type = PacketType::Data;
    assert!(!svc.handle(&pkt).await);
    assert!(svc.get_known_profiles().is_empty());
}

#[tokio::test]
async fn handle_malformed_payload_returns_false() {
    let (svc, _) = new_svc();
    let mut pkt = profile_packet("aether:bob:02", "Bob", "", "", 1);
    pkt.payload = b"not json".to_vec();
    assert!(!svc.handle(&pkt).await);
    assert!(svc.get_known_profiles().is_empty());
}
