// SPDX-License-Identifier: MIT
//! Integration tests for the VoiceCallService.

#[path = "common.rs"]
mod common;

use std::sync::Arc;
use uuid::Uuid;

use aethernet_protocol::{
    extensibility::NoopIncentiveProvider,
    protocol::{MeshPacket, PacketType},
    routing::{verifier::AcceptAllRouteReplyVerifier, InMemoryRouteStore, RoutingService},
    voice::{CallState, VoiceCallService, VoiceSignalingMessage},
};
use common::FakeMeshSender;

const LOCAL: &str = "alice";

async fn new_voice_svc() -> (VoiceCallService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    let routing = Arc::new(RoutingService::with_dependencies(
        sender.clone(),
        store,
        Arc::new(AcceptAllRouteReplyVerifier),
        Arc::new(NoopIncentiveProvider),
    ));
    let svc = VoiceCallService::new(sender.clone(), routing);
    (svc, sender)
}

/// Build a VoiceSignaling packet with the given JSON payload.
fn signaling_pkt(from: &str, to: &str, msg: &VoiceSignalingMessage) -> MeshPacket {
    let payload = serde_json::to_vec(msg).expect("serialize signaling");
    let mut pkt = MeshPacket::new(PacketType::VoiceSignaling, from.to_string());
    pkt.destination_uhid = to.to_string();
    pkt.payload = payload;
    pkt
}

// ── send_offer ──────────────────────────────────────────────────────────

#[tokio::test]
async fn send_offer_returns_call_id() {
    let (svc, _sender) = new_voice_svc().await;
    let cid = svc.send_offer("bob", vec!["opus".into()], 48_000).await;
    assert!(cid.is_ok(), "send_offer should succeed");
    let cid = cid.unwrap();
    assert_ne!(cid, Uuid::nil(), "call id should be non-zero UUID");
}

#[tokio::test]
async fn send_offer_empty_uhid_returns_error() {
    let (svc, _) = new_voice_svc().await;
    let result = svc.send_offer("", vec!["opus".into()], 48_000).await;
    assert!(result.is_err(), "empty to_uhid must return Err");
}

#[tokio::test]
async fn send_offer_emits_voice_signaling_packet() {
    let (svc, sender) = new_voice_svc().await;
    let _ = svc.send_offer("bob", vec!["opus".into()], 48_000).await;
    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::VoiceSignaling);
    let msg: VoiceSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).expect("parse signaling");
    assert_eq!(msg.kind, "offer");
}

// ── handle_packet — inbound offer ──────────────────────────────────────

#[tokio::test]
async fn handle_inbound_offer_creates_incoming_call() {
    let (svc, _) = new_voice_svc().await;
    let call_id = Uuid::new_v4();
    let msg = VoiceSignalingMessage {
        kind: "offer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: Some(vec!["opus".into()]),
        selected_codec: None,
        sample_rate_hz: Some(48_000),
        reason: None,
    };
    let pkt = signaling_pkt("bob", LOCAL, &msg);
    svc.handle_packet(&pkt).await.expect("handle_packet ok");

    // Verify the call is now in Incoming state by trying to accept it.
    svc.accept_call(call_id).await.expect("accept_call should succeed on Incoming call");
}

// ── handle_packet — inbound answer ─────────────────────────────────────

#[tokio::test]
async fn handle_inbound_answer_transitions_outgoing_to_connected() {
    let (svc, _) = new_voice_svc().await;
    let call_id = svc.send_offer("bob", vec!["opus".into()], 48_000).await.unwrap();

    let msg = VoiceSignalingMessage {
        kind: "answer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: Some("opus".into()),
        sample_rate_hz: None,
        reason: None,
    };
    let pkt = signaling_pkt("bob", LOCAL, &msg);
    svc.handle_packet(&pkt).await.expect("handle answer ok");

    // If connected, send_frame should succeed.
    let result = svc.send_frame(call_id, &[0xAA, 0xBB], false).await;
    assert!(result.is_ok(), "send_frame should succeed on connected call");
}

// ── handle_packet — inbound hangup ─────────────────────────────────────

#[tokio::test]
async fn handle_inbound_hangup_transitions_call_to_ended() {
    let (svc, _) = new_voice_svc().await;
    // Receive an inbound offer first.
    let call_id = Uuid::new_v4();
    let offer_msg = VoiceSignalingMessage {
        kind: "offer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: None,
        sample_rate_hz: None,
        reason: None,
    };
    svc.handle_packet(&signaling_pkt("bob", LOCAL, &offer_msg))
        .await
        .unwrap();

    let hangup_msg = VoiceSignalingMessage {
        kind: "hangup".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: None,
        sample_rate_hz: None,
        reason: None,
    };
    svc.handle_packet(&signaling_pkt("bob", LOCAL, &hangup_msg))
        .await
        .unwrap();

    // After hangup, accept_call should fail (not Incoming anymore).
    let result = svc.accept_call(call_id).await;
    assert!(result.is_err(), "accept_call after hangup must fail");
}

// ── accept_call ────────────────────────────────────────────────────────

#[tokio::test]
async fn accept_call_sends_answer_signaling() {
    let (svc, sender) = new_voice_svc().await;
    let call_id = Uuid::new_v4();
    let offer_msg = VoiceSignalingMessage {
        kind: "offer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: None,
        sample_rate_hz: None,
        reason: None,
    };
    svc.handle_packet(&signaling_pkt("bob", LOCAL, &offer_msg))
        .await
        .unwrap();
    sender.clear();

    svc.accept_call(call_id).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::VoiceSignaling);
    let reply: VoiceSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(reply.kind, "answer");
}

#[tokio::test]
async fn accept_call_unknown_id_returns_error() {
    let (svc, _) = new_voice_svc().await;
    let result = svc.accept_call(Uuid::new_v4()).await;
    assert!(result.is_err(), "accept_call on unknown id must fail");
}

// ── hang_up ────────────────────────────────────────────────────────────

#[tokio::test]
async fn hang_up_sends_hangup_signaling() {
    let (svc, sender) = new_voice_svc().await;
    let call_id = svc.send_offer("bob", vec!["opus".into()], 48_000).await.unwrap();
    sender.clear();

    svc.hang_up(call_id).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::VoiceSignaling);
    let msg: VoiceSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(msg.kind, "hangup");
}

#[tokio::test]
async fn hang_up_unknown_id_returns_error() {
    let (svc, _) = new_voice_svc().await;
    let result = svc.hang_up(Uuid::new_v4()).await;
    assert!(result.is_err(), "hang_up on unknown id must fail");
}

// ── send_frame ─────────────────────────────────────────────────────────

#[tokio::test]
async fn send_frame_not_connected_returns_error() {
    let (svc, _) = new_voice_svc().await;
    let call_id = svc.send_offer("bob", vec!["opus".into()], 48_000).await.unwrap();
    // Still Outgoing — no answer received.
    let result = svc.send_frame(call_id, &[0x01, 0x02, 0x03], false).await;
    assert!(result.is_err(), "send_frame on Outgoing call must fail");
}

#[tokio::test]
async fn send_frame_connected_emits_voice_call_packet() {
    let (svc, sender) = new_voice_svc().await;
    let call_id = svc.send_offer("bob", vec!["opus".into()], 48_000).await.unwrap();

    // Simulate bob answering.
    let answer_msg = VoiceSignalingMessage {
        kind: "answer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: None,
        sample_rate_hz: None,
        reason: None,
    };
    svc.handle_packet(&signaling_pkt("bob", LOCAL, &answer_msg))
        .await
        .unwrap();
    sender.clear();

    let audio = [0xDE, 0xAD, 0xBE, 0xEF];
    svc.send_frame(call_id, &audio, false).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::VoiceCall);
    assert!(unicasts[0].packet.payload.len() >= 29 + audio.len());
}
