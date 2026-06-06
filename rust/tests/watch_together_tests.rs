// SPDX-License-Identifier: MIT
//! Integration tests for the WatchTogetherService.

#[path = "common.rs"]
mod common;

use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

use aethermesh_protocol::{
    extensibility::NoopIncentiveProvider,
    protocol::{MeshPacket, PacketType},
    routing::{verifier::AcceptAllRouteReplyVerifier, InMemoryRouteStore, RoutingService},
    streaming::{WatchReactionPayload, WatchSyncPayload, WatchTogetherService},
};
use common::FakeMeshSender;

const LOCAL: &str = "alice";

async fn new_watch_svc() -> (WatchTogetherService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    let routing = Arc::new(RoutingService::with_dependencies(
        sender.clone(),
        store,
        Arc::new(AcceptAllRouteReplyVerifier),
        Arc::new(NoopIncentiveProvider),
    ));
    let svc = WatchTogetherService::new(sender.clone(), routing);
    (svc, sender)
}

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}

/// Build a WatchSync packet carrying the given payload.
fn watch_sync_pkt(from: &str, to: &str, payload: &WatchSyncPayload) -> MeshPacket {
    let body = serde_json::to_vec(payload).expect("serialize watch sync");
    let mut pkt = MeshPacket::new(PacketType::WatchSync, from.to_string());
    pkt.destination_uhid = to.to_string();
    pkt.payload = body;
    pkt
}

// ── invite_to_session ──────────────────────────────────────────────────────────

#[tokio::test]
async fn invite_to_session_sends_join_sync_to_each_member() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "content-1", &["bob".to_string(), "carol".to_string()])
        .await
        .unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 2, "invite must send watchSync to each invitee");
    let targets: std::collections::HashSet<_> =
        unicasts.iter().map(|u| u.next_hop_uhid.as_str()).collect();
    assert!(targets.contains("bob"));
    assert!(targets.contains("carol"));
    for u in &unicasts {
        assert_eq!(u.packet.packet_type, PacketType::WatchSync);
    }
}

#[tokio::test]
async fn invite_to_session_empty_members_returns_error() {
    let (svc, _) = new_watch_svc().await;
    let result = svc.invite_to_session(Uuid::new_v4(), "content-1", &[]).await;
    assert!(result.is_err(), "empty member_uhids must return Err");
}

#[tokio::test]
async fn invite_to_session_payload_contains_content_id_and_kind_join() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "my-video-42", &["bob".to_string()])
        .await
        .unwrap();

    let sync: WatchSyncPayload =
        serde_json::from_slice(&sender.unicasts()[0].packet.payload).unwrap();
    assert_eq!(sync.kind, "join");
    assert_eq!(sync.session_id, sid);
    assert_eq!(sync.content_id, Some("my-video-42".to_string()));
}

// ── play ───────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn play_sends_watch_sync_to_all_members() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "c1", &["bob".to_string()])
        .await
        .unwrap();
    sender.clear();

    svc.play(sid, 5000).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1, "play must send watchSync to bob");
    assert_eq!(unicasts[0].packet.packet_type, PacketType::WatchSync);
    assert_eq!(unicasts[0].next_hop_uhid, "bob");
    let sync: WatchSyncPayload =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(sync.kind, "play");
    assert_eq!(sync.position_ms, Some(5000));
}

#[tokio::test]
async fn play_unknown_session_returns_error() {
    let (svc, _) = new_watch_svc().await;
    let result = svc.play(Uuid::new_v4(), 0).await;
    assert!(result.is_err(), "play on unknown session must fail");
}

// ── pause ──────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn pause_sends_watch_sync_with_kind_pause() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "c1", &["bob".to_string()])
        .await
        .unwrap();
    sender.clear();

    svc.pause(sid, 12000).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    let sync: WatchSyncPayload =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(sync.kind, "pause");
    assert_eq!(sync.position_ms, Some(12000));
}

// ── seek ───────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn seek_sends_watch_sync_with_kind_seek_and_correct_position() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "c1", &["bob".to_string()])
        .await
        .unwrap();
    sender.clear();

    svc.seek(sid, 30000).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    let sync: WatchSyncPayload =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(sync.kind, "seek");
    assert_eq!(sync.position_ms, Some(30000));
}

// ── set_speed ─────────────────────────────────────────────────────────────────

#[tokio::test]
async fn set_speed_sends_watch_sync_with_kind_speed() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "c1", &["bob".to_string()])
        .await
        .unwrap();
    sender.clear();

    svc.set_speed(sid, 1.5).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    let sync: WatchSyncPayload =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(sync.kind, "speed");
    assert_eq!(sync.playback_speed, Some(1.5));
}

// ── send_reaction ─────────────────────────────────────────────────────────────

#[tokio::test]
async fn send_reaction_sends_watch_reaction_to_all_members() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "c1", &["bob".to_string(), "carol".to_string()])
        .await
        .unwrap();
    sender.clear();

    svc.send_reaction(sid, "🔥").await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 2, "reaction must reach both members");
    let targets: std::collections::HashSet<_> =
        unicasts.iter().map(|u| u.next_hop_uhid.as_str()).collect();
    assert!(targets.contains("bob"));
    assert!(targets.contains("carol"));
    assert!(!targets.contains(LOCAL), "reaction must not send to self");
    for u in &unicasts {
        assert_eq!(u.packet.packet_type, PacketType::WatchReaction);
        let payload: WatchReactionPayload =
            serde_json::from_slice(&u.packet.payload).unwrap();
        assert_eq!(payload.reaction, "🔥");
    }
}

#[tokio::test]
async fn send_reaction_unknown_session_returns_error() {
    let (svc, _) = new_watch_svc().await;
    let result = svc.send_reaction(Uuid::new_v4(), "❤️").await;
    assert!(result.is_err(), "reaction on unknown session must fail");
}

// ── handle_packet — inbound join ───────────────────────────────────────────────

#[tokio::test]
async fn handle_packet_inbound_join_adds_member_for_future_broadcasts() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "c1", &["bob".to_string()])
        .await
        .unwrap();

    // Carol sends an inbound join sync.
    let join_sync = WatchSyncPayload {
        session_id: sid,
        kind: "join".into(),
        position_ms: Some(0),
        playback_speed: Some(1.0),
        sent_at_ms: now_ms(),
        content_id: None,
    };
    svc.handle_packet(&watch_sync_pkt("carol", LOCAL, &join_sync))
        .await
        .unwrap();
    sender.clear();

    // send_reaction must reach carol too (she joined the session).
    svc.send_reaction(sid, "👍").await.unwrap();
    let targets: std::collections::HashSet<_> =
        sender.unicasts().iter().map(|u| u.next_hop_uhid.clone()).collect();
    assert!(targets.contains("carol"), "carol joined so she must receive reaction");
}

// ── handle_packet — inbound leave ─────────────────────────────────────────────

#[tokio::test]
async fn handle_packet_inbound_leave_removes_member() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "c1", &["bob".to_string()])
        .await
        .unwrap();

    // Bob sends a leave sync.
    let leave_sync = WatchSyncPayload {
        session_id: sid,
        kind: "leave".into(),
        position_ms: None,
        playback_speed: None,
        sent_at_ms: now_ms(),
        content_id: None,
    };
    svc.handle_packet(&watch_sync_pkt("bob", LOCAL, &leave_sync))
        .await
        .unwrap();
    sender.clear();

    // After bob left, send_reaction must not send to him.
    svc.send_reaction(sid, "👋").await.unwrap();
    let targets: std::collections::HashSet<_> =
        sender.unicasts().iter().map(|u| u.next_hop_uhid.clone()).collect();
    assert!(!targets.contains("bob"), "bob left so he must not receive reaction");
}

// ── handle_packet — inbound play ──────────────────────────────────────────────

#[tokio::test]
async fn handle_packet_inbound_play_does_not_error() {
    let (svc, sender) = new_watch_svc().await;
    let sid = Uuid::new_v4();
    svc.invite_to_session(sid, "c1", &["bob".to_string()])
        .await
        .unwrap();

    let play_sync = WatchSyncPayload {
        session_id: sid,
        kind: "play".into(),
        position_ms: Some(10000),
        playback_speed: Some(1.0),
        sent_at_ms: now_ms(),
        content_id: None,
    };
    svc.handle_packet(&watch_sync_pkt("bob", LOCAL, &play_sync))
        .await
        .expect("handle inbound play must not return Err");

    // Session is still operational — subsequent outbound seek must succeed.
    sender.clear();
    svc.seek(sid, 15000).await.expect("seek must succeed after inbound play");
    assert_eq!(sender.unicasts().len(), 1, "seek must reach bob");
}
