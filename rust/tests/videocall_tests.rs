// SPDX-License-Identifier: MIT
//! Integration tests for the VideoCall call-control service (PacketType 27).

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;
use uuid::Uuid;

use aethernet_protocol::{
    constants::DEFAULT_TTL,
    protocol::{MeshPacket, PacketType},
    videocall::VideoCallControlService,
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (VideoCallControlService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = VideoCallControlService::new(sender.clone());
    (svc, sender)
}

fn new_svc() -> (VideoCallControlService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL)
}

/// Build an inbound VideoCall control packet from `source`. Built independently of
/// the service's private wire struct (via serde_json) so the test pins the wire
/// shape, mirroring the C# `ControlPacket` helper.
fn control_packet(call_id: Uuid, action: &str, from_uhid: &str, sent_at_ms: i64) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "call_id": call_id,
        "action": action,
        "sent_at_ms": sent_at_ms,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::VideoCall, from_uhid.to_string());
    p.destination_uhid = LOCAL.to_string();
    p.payload = body;
    p
}

// ─── Ring ───────────────────────────────────────────────

#[tokio::test]
async fn ring_sends_directed_ring_to_peer_and_returns_call_id() {
    let (svc, sender) = new_svc_for("aether:alice:01");

    let call_id = svc.ring("aether:bob:02").await;
    assert_ne!(call_id, Uuid::nil());

    let sends = sender.unicasts();
    assert_eq!(sends.len(), 1, "expected exactly one directed send");
    let sent = &sends[0];
    assert_eq!(sent.packet.packet_type, PacketType::VideoCall);
    assert_eq!(sent.next_hop_uhid, "aether:bob:02");
    assert_eq!(sent.packet.destination_uhid, "aether:bob:02");
    assert_eq!(sent.packet.ttl, DEFAULT_TTL);
    assert_eq!(sent.packet.source_uhid, "aether:alice:01");

    let body: serde_json::Value = serde_json::from_slice(&sent.packet.payload).unwrap();
    assert_eq!(body["action"], json!("ring"));
    assert_eq!(body["call_id"], json!(call_id.to_string()));
}

// ─── Accept / Decline / Hangup ──────────────────────────

#[tokio::test]
async fn accept_sends_directed_accept_to_peer() {
    let (svc, sender) = new_svc();
    let call_id = Uuid::new_v4();

    let ok = svc.accept(call_id, "aether:bob:02").await;
    assert!(ok);

    let sends = sender.unicasts();
    assert_eq!(sends.len(), 1);
    assert_eq!(sends[0].next_hop_uhid, "aether:bob:02");
    let body: serde_json::Value = serde_json::from_slice(&sends[0].packet.payload).unwrap();
    assert_eq!(body["action"], json!("accept"));
    assert_eq!(body["call_id"], json!(call_id.to_string()));
}

#[tokio::test]
async fn decline_sends_directed_decline_to_peer() {
    let (svc, sender) = new_svc();
    let call_id = Uuid::new_v4();

    let ok = svc.decline(call_id, "aether:bob:02").await;
    assert!(ok);

    let sends = sender.unicasts();
    assert_eq!(sends.len(), 1);
    assert_eq!(sends[0].next_hop_uhid, "aether:bob:02");
    let body: serde_json::Value = serde_json::from_slice(&sends[0].packet.payload).unwrap();
    assert_eq!(body["action"], json!("decline"));
    assert_eq!(body["call_id"], json!(call_id.to_string()));
}

#[tokio::test]
async fn hangup_sends_directed_hangup_to_peer() {
    let (svc, sender) = new_svc();
    let call_id = Uuid::new_v4();

    let ok = svc.hangup(call_id, "aether:bob:02").await;
    assert!(ok);

    let sends = sender.unicasts();
    assert_eq!(sends.len(), 1);
    assert_eq!(sends[0].next_hop_uhid, "aether:bob:02");
    let body: serde_json::Value = serde_json::from_slice(&sends[0].packet.payload).unwrap();
    assert_eq!(body["action"], json!("hangup"));
    assert_eq!(body["call_id"], json!(call_id.to_string()));
}

#[tokio::test]
async fn respond_reports_delivery_failure() {
    // FakeMeshSender.send returns false for peers registered as failing.
    let (svc, sender) = new_svc();
    sender.fail_sends_to("aether:bob:02");

    let ok = svc.accept(Uuid::new_v4(), "aether:bob:02").await;
    assert!(!ok, "delivery to a failing peer must report false");
}

// ─── Handle ─────────────────────────────────────────────

#[tokio::test]
async fn handle_raises_call_state_changed() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_state_changed();

    let call_id = Uuid::new_v4();
    let ok = svc
        .handle(&control_packet(call_id, "ring", "aether:bob:02", 1))
        .await;

    assert!(ok);
    let got = events
        .try_recv()
        .expect("expected a call-state-changed event");
    assert_eq!(got.call_id, call_id);
    assert_eq!(got.action, "ring");
    assert_eq!(got.from_uhid, "aether:bob:02");
}

#[tokio::test]
async fn handle_wrong_packet_type_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_state_changed();

    let mut pkt = control_packet(Uuid::new_v4(), "ring", "aether:bob:02", 1);
    pkt.packet_type = PacketType::Data;

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "wrong type must not surface");
}

#[tokio::test]
async fn handle_malformed_payload_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_state_changed();

    let mut pkt = control_packet(Uuid::new_v4(), "ring", "aether:bob:02", 1);
    pkt.payload = b"not json".to_vec();

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "malformed payload must not surface");
}

#[tokio::test]
async fn handle_empty_action_returns_false() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_state_changed();

    let pkt = control_packet(Uuid::new_v4(), "", "aether:bob:02", 1);

    assert!(!svc.handle(&pkt).await);
    assert!(events.try_recv().is_err(), "empty action must not surface");
}
