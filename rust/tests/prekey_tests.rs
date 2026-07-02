// SPDX-License-Identifier: MIT
//! Integration tests for the mesh PreKey exchange service (PacketType PreKeyRequest 25 /
//! PreKeyResponse 26). Directed request/response transport of a `PreKeyBundle` over the mesh.
//! Mirrors the C# `PreKeyExchangeTests`.

#[path = "common.rs"]
mod common;

use base64::{engine::general_purpose::STANDARD, Engine as _};
use serde_json::json;
use std::sync::Arc;
use uuid::Uuid;

use aethernet_protocol::{
    models::PreKeyBundle,
    prekey::PreKeyExchangeService,
    protocol::{MeshPacket, PacketType},
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (PreKeyExchangeService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = PreKeyExchangeService::new(sender.clone());
    (svc, sender)
}

fn sample_bundle(uhid: &str) -> PreKeyBundle {
    PreKeyBundle::new(
        uhid.to_string(),
        vec![0x11; 32],
        vec![0x22; 32],
        4242,
        vec![0x33; 32],
        77,
        vec![0x44; 32],
        vec![0x55; 64],
    )
}

/// Build an inbound PreKeyRequest packet. Built independently of the service's private wire struct
/// (via serde_json) so the test pins the wire shape, mirroring the C# request packet construction.
fn request_packet(request_id: Uuid, requester_uhid: &str, source: &str) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "request_id": request_id,
        "requester_uhid": requester_uhid,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::PreKeyRequest, source.to_string());
    p.destination_uhid = LOCAL.to_string();
    p.payload = body;
    p
}

/// Build an inbound PreKeyResponse packet from a bundle, echoing `request_id`. Byte fields are
/// STANDARD base64, matching the canonical wire (fixtures/prekey/vectors.json).
fn response_packet(request_id: Uuid, bundle: &PreKeyBundle, source: &str) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "request_id": request_id,
        "uhid": bundle.uhid,
        "identity_key": STANDARD.encode(&bundle.identity_key),
        "identity_key_x25519": STANDARD.encode(&bundle.identity_key_x25519),
        "pre_key_id": bundle.pre_key_id,
        "pre_key": STANDARD.encode(&bundle.pre_key),
        "signed_pre_key_id": bundle.signed_pre_key_id,
        "signed_pre_key": STANDARD.encode(&bundle.signed_pre_key),
        "signed_pre_key_signature": STANDARD.encode(&bundle.signed_pre_key_signature),
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::PreKeyResponse, source.to_string());
    p.destination_uhid = LOCAL.to_string();
    p.payload = body;
    p
}

// ─── Request ────────────────────────────────────────────

#[tokio::test]
async fn request_sends_directed_prekey_request_and_returns_id() {
    let (svc, sender) = new_svc_for("aether:alice:01");

    let req_id = svc.request_bundle("aether:bob:02").await;
    assert_ne!(req_id, Uuid::nil());

    let sends = sender.unicasts();
    assert_eq!(sends.len(), 1, "expected exactly one directed send");
    let sent = &sends[0];
    assert_eq!(sent.packet.packet_type, PacketType::PreKeyRequest);
    assert_eq!(sent.next_hop_uhid, "aether:bob:02");
    assert_eq!(sent.packet.destination_uhid, "aether:bob:02");
    assert_eq!(sent.packet.source_uhid, "aether:alice:01");

    let body: serde_json::Value = serde_json::from_slice(&sent.packet.payload).unwrap();
    assert_eq!(body["request_id"], json!(req_id.to_string()));
    assert_eq!(body["requester_uhid"], json!("aether:alice:01"));
}

// ─── Handle request ─────────────────────────────────────

#[tokio::test]
async fn handle_request_with_local_bundle_sends_directed_response_to_requester() {
    let (svc, sender) = new_svc_for("aether:bob:02");
    svc.set_local_bundle(sample_bundle("aether:bob:02"));

    let req_id = Uuid::new_v4();
    let ok = svc
        .handle(&request_packet(req_id, "aether:alice:01", "aether:alice:01"))
        .await;
    assert!(ok);

    let sends = sender.unicasts();
    assert_eq!(sends.len(), 1);
    let sent = &sends[0];
    assert_eq!(sent.packet.packet_type, PacketType::PreKeyResponse);
    assert_eq!(sent.next_hop_uhid, "aether:alice:01");

    let body: serde_json::Value = serde_json::from_slice(&sent.packet.payload).unwrap();
    assert_eq!(body["request_id"], json!(req_id.to_string()));
    assert_eq!(body["uhid"], json!("aether:bob:02"));
    assert_eq!(body["pre_key_id"], json!(4242));
    // 64-byte signature → 88 base64 chars (with padding).
    assert_eq!(body["signed_pre_key_signature"].as_str().unwrap().len(), 88);
}

#[tokio::test]
async fn handle_request_no_local_bundle_returns_false_and_sends_nothing() {
    let (svc, sender) = new_svc_for("aether:bob:02");

    let ok = svc
        .handle(&request_packet(Uuid::new_v4(), "aether:alice:01", "aether:alice:01"))
        .await;

    assert!(!ok);
    assert!(sender.unicasts().is_empty(), "no bundle set → no send");
}

// ─── Handle response ────────────────────────────────────

#[tokio::test]
async fn handle_response_caches_bundle_and_raises_event() {
    let (svc, _) = new_svc_for("aether:alice:01");
    let mut events = svc.subscribe_bundle_received();

    let req_id = Uuid::new_v4();
    let ok = svc
        .handle(&response_packet(req_id, &sample_bundle("aether:bob:02"), "aether:bob:02"))
        .await;
    assert!(ok);

    let got = events.try_recv().expect("expected a bundle-received event");
    assert_eq!(got.request_id, req_id);
    assert_eq!(got.from_uhid, "aether:bob:02");
    assert_eq!(got.bundle.uhid, "aether:bob:02");

    let cached = svc.get_received_bundle("aether:bob:02").expect("bundle cached");
    assert_eq!(cached.pre_key_id, 4242);
    assert_eq!(cached.signed_pre_key_id, 77);
    assert_eq!(cached.identity_key, vec![0x11; 32]);
    assert_eq!(cached.signed_pre_key_signature.len(), 64);
}

// ─── Wrong type ─────────────────────────────────────────

#[tokio::test]
async fn handle_wrong_packet_type_returns_false() {
    let (svc, _) = new_svc_for(LOCAL);
    let mut events = svc.subscribe_bundle_received();

    let mut pkt = request_packet(Uuid::new_v4(), "aether:alice:01", "aether:x:01");
    pkt.packet_type = PacketType::Data;

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "wrong type must not surface");
}

// ─── Round-trip through bundle ──────────────────────────

#[tokio::test]
async fn response_round_trips_through_bundle() {
    // Responder serves its bundle; requester caches an identical bundle after decode.
    let (responder, resp_sender) = new_svc_for("aether:bob:02");
    responder.set_local_bundle(sample_bundle("aether:bob:02"));

    let req_id = Uuid::new_v4();
    assert!(
        responder
            .handle(&request_packet(req_id, "aether:alice:01", "aether:alice:01"))
            .await
    );
    let response = resp_sender.unicasts().remove(0).packet;

    let (requester, _) = new_svc_for("aether:alice:01");
    assert!(requester.handle(&response).await);

    let original = sample_bundle("aether:bob:02");
    let back = requester.get_received_bundle("aether:bob:02").expect("cached");
    assert_eq!(back.uhid, original.uhid);
    assert_eq!(back.pre_key_id, original.pre_key_id);
    assert_eq!(back.signed_pre_key_id, original.signed_pre_key_id);
    assert_eq!(back.identity_key, original.identity_key);
    assert_eq!(back.identity_key_x25519, original.identity_key_x25519);
    assert_eq!(back.pre_key, original.pre_key);
    assert_eq!(back.signed_pre_key, original.signed_pre_key);
    assert_eq!(back.signed_pre_key_signature, original.signed_pre_key_signature);
}

// ─── Local bundle accessors ─────────────────────────────

#[tokio::test]
async fn set_and_get_local_bundle() {
    let (svc, _) = new_svc_for(LOCAL);
    assert!(svc.get_local_bundle().is_none());

    svc.set_local_bundle(sample_bundle("aether:local:01"));
    let got = svc.get_local_bundle().expect("bundle set");
    assert_eq!(got.uhid, "aether:local:01");
    assert_eq!(got.pre_key_id, 4242);
}
