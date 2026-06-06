// SPDX-License-Identifier: MIT
//! Integration tests for the VideoCallService.

#[path = "common.rs"]
mod common;

use std::sync::Arc;
use uuid::Uuid;

use aethermesh_protocol::{
    extensibility::NoopIncentiveProvider,
    protocol::{MeshPacket, PacketType},
    routing::{verifier::AcceptAllRouteReplyVerifier, InMemoryRouteStore, RoutingService},
    streaming::{VideoCallService, VideoSignalingMessage},
};
use common::FakeMeshSender;

const LOCAL: &str = "alice";

async fn new_video_svc() -> (VideoCallService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    let routing = Arc::new(RoutingService::with_dependencies(
        sender.clone(),
        store,
        Arc::new(AcceptAllRouteReplyVerifier),
        Arc::new(NoopIncentiveProvider),
    ));
    let svc = VideoCallService::new(sender.clone(), routing);
    (svc, sender)
}

/// Build a VideoSignaling packet with the given message.
fn video_signaling_pkt(from: &str, to: &str, msg: &VideoSignalingMessage) -> MeshPacket {
    let payload = serde_json::to_vec(msg).expect("serialize video signaling");
    let mut pkt = MeshPacket::new(PacketType::VideoSignaling, from.to_string());
    pkt.destination_uhid = to.to_string();
    pkt.payload = payload;
    pkt
}

// ── send_offer ─────────────────────────────────────────────────────────────────

#[tokio::test]
async fn send_offer_returns_call_id() {
    let (svc, _) = new_video_svc().await;
    let result = svc.send_offer("bob", vec!["h264".into()], 1280, 720, 30, 1000).await;
    assert!(result.is_ok(), "send_offer should succeed");
    assert_ne!(result.unwrap(), Uuid::nil());
}

#[tokio::test]
async fn send_offer_empty_uhid_returns_error() {
    let (svc, _) = new_video_svc().await;
    let result = svc.send_offer("", vec!["h264".into()], 1280, 720, 30, 1000).await;
    assert!(result.is_err(), "empty to_uhid must return Err");
}

#[tokio::test]
async fn send_offer_emits_video_signaling_packet() {
    let (svc, sender) = new_video_svc().await;
    let _ = svc.send_offer("bob", vec!["h264".into()], 1280, 720, 30, 1000).await.unwrap();
    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::VideoSignaling);
    let msg: VideoSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).expect("parse video signaling");
    assert_eq!(msg.kind, "offer");
    assert_eq!(msg.proposed_codecs, Some(vec!["h264".to_string()]));
}

// ── handle_packet — inbound offer ─────────────────────────────────────────────

#[tokio::test]
async fn handle_inbound_offer_creates_incoming_call() {
    let (svc, _) = new_video_svc().await;
    let call_id = Uuid::new_v4();
    let msg = VideoSignalingMessage {
        kind: "offer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: Some(vec!["h264".into()]),
        selected_codec: None,
        width: Some(1280),
        height: Some(720),
        fps: Some(30),
        bitrate_kbps: Some(1000),
        reason: None,
    };
    let pkt = video_signaling_pkt("bob", LOCAL, &msg);
    svc.handle_packet(&pkt).await.expect("handle offer ok");

    // If the call is now Incoming, accept_call should succeed.
    svc.accept_call(call_id).await.expect("accept should succeed on Incoming call");
}

// ── handle_packet — inbound answer ────────────────────────────────────────────

#[tokio::test]
async fn handle_inbound_answer_transitions_outgoing_to_connected() {
    let (svc, _) = new_video_svc().await;
    let call_id = svc
        .send_offer("bob", vec!["h264".into()], 1280, 720, 30, 1000)
        .await
        .unwrap();

    let msg = VideoSignalingMessage {
        kind: "answer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: Some("h264".into()),
        width: None,
        height: None,
        fps: None,
        bitrate_kbps: None,
        reason: None,
    };
    svc.handle_packet(&video_signaling_pkt("bob", LOCAL, &msg))
        .await
        .expect("handle answer ok");

    // If connected, send_frame should succeed.
    let result = svc.send_frame(call_id, &[0xAA, 0xBB], false).await;
    assert!(result.is_ok(), "send_frame should succeed on connected call");
}

// ── handle_packet — inbound hangup ────────────────────────────────────────────

#[tokio::test]
async fn handle_inbound_hangup_transitions_call_to_ended() {
    let (svc, _) = new_video_svc().await;
    let call_id = Uuid::new_v4();
    let offer_msg = VideoSignalingMessage {
        kind: "offer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: None,
        width: None,
        height: None,
        fps: None,
        bitrate_kbps: None,
        reason: None,
    };
    svc.handle_packet(&video_signaling_pkt("bob", LOCAL, &offer_msg))
        .await
        .unwrap();

    let hangup_msg = VideoSignalingMessage {
        kind: "hangup".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: None,
        width: None,
        height: None,
        fps: None,
        bitrate_kbps: None,
        reason: None,
    };
    svc.handle_packet(&video_signaling_pkt("bob", LOCAL, &hangup_msg))
        .await
        .unwrap();

    // After hangup the call is Ended — accept should now fail.
    let result = svc.accept_call(call_id).await;
    assert!(result.is_err(), "accept_call after hangup must fail");
}

// ── accept_call ────────────────────────────────────────────────────────────────

#[tokio::test]
async fn accept_call_sends_answer_signaling() {
    let (svc, sender) = new_video_svc().await;
    let call_id = Uuid::new_v4();
    let offer_msg = VideoSignalingMessage {
        kind: "offer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: None,
        width: None,
        height: None,
        fps: None,
        bitrate_kbps: None,
        reason: None,
    };
    svc.handle_packet(&video_signaling_pkt("bob", LOCAL, &offer_msg))
        .await
        .unwrap();
    sender.clear();

    svc.accept_call(call_id).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::VideoSignaling);
    let reply: VideoSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(reply.kind, "answer");
}

#[tokio::test]
async fn accept_call_unknown_id_returns_error() {
    let (svc, _) = new_video_svc().await;
    let result = svc.accept_call(Uuid::new_v4()).await;
    assert!(result.is_err(), "accept_call on unknown id must fail");
}

// ── hang_up ────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn hang_up_sends_hangup_signaling() {
    let (svc, sender) = new_video_svc().await;
    let call_id = svc
        .send_offer("bob", vec!["h264".into()], 1280, 720, 30, 1000)
        .await
        .unwrap();
    sender.clear();

    svc.hang_up(call_id).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::VideoSignaling);
    let msg: VideoSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(msg.kind, "hangup");
}

#[tokio::test]
async fn hang_up_unknown_id_returns_error() {
    let (svc, _) = new_video_svc().await;
    let result = svc.hang_up(Uuid::new_v4()).await;
    assert!(result.is_err(), "hang_up on unknown id must fail");
}

// ── send_frame ─────────────────────────────────────────────────────────────────

#[tokio::test]
async fn send_frame_not_connected_returns_error() {
    let (svc, _) = new_video_svc().await;
    let call_id = svc
        .send_offer("bob", vec!["h264".into()], 1280, 720, 30, 1000)
        .await
        .unwrap();
    // Still Outgoing — no answer received.
    let result = svc.send_frame(call_id, &[0x01, 0x02], false).await;
    assert!(result.is_err(), "send_frame on Outgoing call must fail");
}

#[tokio::test]
async fn send_frame_connected_emits_video_frame_packet() {
    let (svc, sender) = new_video_svc().await;
    let call_id = svc
        .send_offer("bob", vec!["h264".into()], 1280, 720, 30, 1000)
        .await
        .unwrap();

    // Simulate bob answering.
    let answer_msg = VideoSignalingMessage {
        kind: "answer".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        proposed_codecs: None,
        selected_codec: None,
        width: None,
        height: None,
        fps: None,
        bitrate_kbps: None,
        reason: None,
    };
    svc.handle_packet(&video_signaling_pkt("bob", LOCAL, &answer_msg))
        .await
        .unwrap();
    sender.clear();

    let video = [0xDE, 0xAD, 0xBE, 0xEF];
    svc.send_frame(call_id, &video, true).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::VideoFrame);
    // Wire: [16 callId][4 seq][8 ts][1 isKeyframe][N video]
    assert!(unicasts[0].packet.payload.len() >= 29 + video.len());
    // isKeyframe byte at offset 28 must be 1
    assert_eq!(unicasts[0].packet.payload[28], 1u8, "isKeyframe flag must be set");
}

// ── request_keyframe ──────────────────────────────────────────────────────────

#[tokio::test]
async fn request_keyframe_sends_keyframe_request_signaling() {
    let (svc, sender) = new_video_svc().await;
    let call_id = svc
        .send_offer("bob", vec!["h264".into()], 1280, 720, 30, 1000)
        .await
        .unwrap();
    sender.clear();

    svc.request_keyframe(call_id).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    let msg: VideoSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(msg.kind, "keyframe_request");
}

// ── notify_quality_change ─────────────────────────────────────────────────────

#[tokio::test]
async fn notify_quality_change_sends_quality_change_signaling() {
    let (svc, sender) = new_video_svc().await;
    let call_id = svc
        .send_offer("bob", vec!["h264".into()], 1280, 720, 30, 1000)
        .await
        .unwrap();
    sender.clear();

    svc.notify_quality_change(call_id, 640, 480, 15, 500).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    let msg: VideoSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(msg.kind, "quality_change");
    assert_eq!(msg.width, Some(640));
    assert_eq!(msg.height, Some(480));
    assert_eq!(msg.fps, Some(15));
    assert_eq!(msg.bitrate_kbps, Some(500));
}
