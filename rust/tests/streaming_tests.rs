// SPDX-License-Identifier: MIT
//! Integration tests for the StreamingService.

#[path = "common.rs"]
mod common;

use std::sync::Arc;
use uuid::Uuid;

use aethernet_protocol::{
    extensibility::NoopIncentiveProvider,
    protocol::{MeshPacket, PacketType},
    routing::{verifier::AcceptAllRouteReplyVerifier, InMemoryRouteStore, RoutingService},
    streaming::{StreamSubscribePayload, StreamUnsubscribePayload, StreamingService},
};
use common::FakeMeshSender;

const LOCAL: &str = "alice";

async fn new_streaming_svc() -> (StreamingService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    let routing = Arc::new(RoutingService::with_dependencies(
        sender.clone(),
        store,
        Arc::new(AcceptAllRouteReplyVerifier),
        Arc::new(NoopIncentiveProvider),
    ));
    let svc = StreamingService::new(sender.clone(), routing);
    (svc, sender)
}

/// Build a StreamSubscribe packet from bob → alice for the given stream.
fn subscribe_pkt(subscriber: &str, stream_id: Uuid) -> MeshPacket {
    let payload = serde_json::to_vec(&StreamSubscribePayload {
        stream_id,
        live_only: false,
    })
    .expect("serialize subscribe");
    let mut pkt = MeshPacket::new(PacketType::StreamSubscribe, subscriber.to_string());
    pkt.payload = payload;
    pkt
}

/// Build a StreamUnsubscribe packet.
fn unsubscribe_pkt(subscriber: &str, stream_id: Uuid) -> MeshPacket {
    let payload = serde_json::to_vec(&StreamUnsubscribePayload { stream_id })
        .expect("serialize unsubscribe");
    let mut pkt = MeshPacket::new(PacketType::StreamUnsubscribe, subscriber.to_string());
    pkt.payload = payload;
    pkt
}

// ── start_stream ───────────────────────────────────────────────────────

#[tokio::test]
async fn start_stream_returns_non_nil_uuid() {
    let (svc, _) = new_streaming_svc().await;
    let sid = svc.start_stream("My Stream", "video/h264", "h264", 2_000).await;
    assert!(sid.is_ok());
    assert_ne!(sid.unwrap(), Uuid::nil());
}

#[tokio::test]
async fn start_stream_broadcasts_stream_announce() {
    let (svc, sender) = new_streaming_svc().await;
    let _ = svc.start_stream("Live", "audio/opus", "opus", 1_000).await.unwrap();
    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1, "expected exactly one broadcast");
    assert_eq!(bcasts[0].packet_type, PacketType::StreamAnnounce);
    // Announce payload must include the stream id and state=live
    let body: serde_json::Value =
        serde_json::from_slice(&bcasts[0].payload).expect("parse announce payload");
    assert_eq!(body["state"], "live");
    assert!(body["stream_id"].is_string(), "stream_id must be present");
}

// ── end_stream ─────────────────────────────────────────────────────────

#[tokio::test]
async fn end_stream_broadcasts_ended_announce() {
    let (svc, sender) = new_streaming_svc().await;
    let sid = svc.start_stream("Live", "audio/opus", "opus", 1_000).await.unwrap();
    sender.clear();

    svc.end_stream(sid).await.unwrap();

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1);
    assert_eq!(bcasts[0].packet_type, PacketType::StreamAnnounce);
    let body: serde_json::Value = serde_json::from_slice(&bcasts[0].payload).unwrap();
    assert_eq!(body["state"], "ended");
}

#[tokio::test]
async fn end_stream_unknown_id_returns_error() {
    let (svc, _) = new_streaming_svc().await;
    let result = svc.end_stream(Uuid::new_v4()).await;
    assert!(result.is_err(), "ending unknown stream must fail");
}

// ── subscribe / unsubscribe ────────────────────────────────────────────

#[tokio::test]
async fn subscribe_sends_stream_subscribe_packet_to_publisher() {
    let (svc, sender) = new_streaming_svc().await;
    let stream_id = Uuid::new_v4();
    svc.subscribe(stream_id, "bob", false).await.unwrap();
    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::StreamSubscribe);
    let payload: StreamSubscribePayload =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(payload.stream_id, stream_id);
}

#[tokio::test]
async fn subscribe_empty_publisher_returns_error() {
    let (svc, _) = new_streaming_svc().await;
    let result = svc.subscribe(Uuid::new_v4(), "", false).await;
    assert!(result.is_err());
}

#[tokio::test]
async fn unsubscribe_sends_stream_unsubscribe_packet() {
    let (svc, sender) = new_streaming_svc().await;
    let stream_id = Uuid::new_v4();
    svc.subscribe(stream_id, "bob", false).await.unwrap();
    sender.clear();
    svc.unsubscribe(stream_id, "bob").await.unwrap();
    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    assert_eq!(unicasts[0].packet.packet_type, PacketType::StreamUnsubscribe);
}

// ── handle_packet — subscribe / unsubscribe ─────────────────────────────

#[tokio::test]
async fn handle_subscribe_adds_subscriber_so_publish_reaches_them() {
    let (svc, sender) = new_streaming_svc().await;
    let sid = svc.start_stream("T", "video/h264", "h264", 2_000).await.unwrap();

    // Bob subscribes via inbound packet.
    svc.handle_packet(&subscribe_pkt("bob", sid)).await.unwrap();
    sender.clear();

    svc.publish_segment(sid, &[0x01, 0x02], true).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1, "exactly one segment unicast to bob");
    assert_eq!(unicasts[0].next_hop_uhid, "bob");
    assert_eq!(unicasts[0].packet.packet_type, PacketType::StreamSegment);
}

#[tokio::test]
async fn handle_unsubscribe_removes_subscriber_so_publish_skips_them() {
    let (svc, sender) = new_streaming_svc().await;
    let sid = svc.start_stream("T", "video/h264", "h264", 2_000).await.unwrap();

    svc.handle_packet(&subscribe_pkt("bob", sid)).await.unwrap();
    svc.handle_packet(&unsubscribe_pkt("bob", sid)).await.unwrap();
    sender.clear();

    svc.publish_segment(sid, &[0xAA], false).await.unwrap();
    // Bob unsubscribed — no unicasts expected.
    assert!(sender.unicasts().is_empty(), "no segment after unsubscribe");
}

// ── publish_segment ────────────────────────────────────────────────────

#[tokio::test]
async fn publish_segment_fans_out_to_multiple_subscribers() {
    let (svc, sender) = new_streaming_svc().await;
    let sid = svc.start_stream("T", "audio/opus", "opus", 500).await.unwrap();

    svc.handle_packet(&subscribe_pkt("bob", sid)).await.unwrap();
    svc.handle_packet(&subscribe_pkt("carol", sid)).await.unwrap();
    sender.clear();

    svc.publish_segment(sid, &[0x01], false).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 2, "segment should reach both subscribers");
    let targets: std::collections::HashSet<_> =
        unicasts.iter().map(|u| u.next_hop_uhid.as_str()).collect();
    assert!(targets.contains("bob"));
    assert!(targets.contains("carol"));
}

#[tokio::test]
async fn publish_segment_inactive_stream_returns_error() {
    let (svc, _) = new_streaming_svc().await;
    let result = svc.publish_segment(Uuid::new_v4(), &[0x01], false).await;
    assert!(result.is_err(), "publish on unknown/inactive stream must fail");
}

#[tokio::test]
async fn publish_segment_ended_stream_returns_error() {
    let (svc, _) = new_streaming_svc().await;
    let sid = svc.start_stream("T", "audio/opus", "opus", 500).await.unwrap();
    svc.end_stream(sid).await.unwrap();
    let result = svc.publish_segment(sid, &[0x01], false).await;
    assert!(result.is_err(), "publish on ended stream must fail");
}

#[tokio::test]
async fn publish_segment_payload_has_correct_wire_format() {
    let (svc, sender) = new_streaming_svc().await;
    let sid = svc.start_stream("T", "video/h264", "h264", 2_000).await.unwrap();
    svc.handle_packet(&subscribe_pkt("bob", sid)).await.unwrap();
    sender.clear();

    let video = [0x11, 0x22, 0x33, 0x44];
    svc.publish_segment(sid, &video, true).await.unwrap();

    let pkt = &sender.unicasts()[0].packet;
    // Wire: [16 stream_id][4 seq][8 ts][1 is_keyframe][N data]
    assert!(pkt.payload.len() >= 29 + video.len());
    // is_keyframe byte at offset 28 should be 1.
    assert_eq!(pkt.payload[28], 1u8, "is_keyframe flag should be set");
    // last bytes should be our video data.
    let n = pkt.payload.len();
    assert_eq!(&pkt.payload[n - video.len()..], &video);
}
