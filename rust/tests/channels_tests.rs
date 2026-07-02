// SPDX-License-Identifier: MIT
//! Integration tests for the ChannelMessage service (PacketType 7).

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;
use uuid::Uuid;

use aethernet_protocol::{
    channels::ChannelMessageService,
    constants::DEFAULT_TTL,
    models::PeerInfo,
    protocol::{MeshPacket, PacketType},
};
use common::FakeMeshSender;

const LOCAL: &str = "aether:local:01";

fn new_svc_for(local: &str) -> (ChannelMessageService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = ChannelMessageService::new(sender.clone());
    (svc, sender)
}

fn new_svc() -> (ChannelMessageService, Arc<FakeMeshSender>) {
    new_svc_for(LOCAL)
}

/// Build an inbound ChannelMessage packet from `source`. Built independently of
/// the service's private wire struct (via serde_json) so the test pins the wire
/// shape, mirroring the C# `ChannelPacket` helper.
fn channel_packet(
    channel_id: &str,
    message_id: Uuid,
    sender: &str,
    content: &str,
    sent_at_ms: i64,
    ttl: i32,
) -> MeshPacket {
    let body = serde_json::to_vec(&json!({
        "channel_id": channel_id,
        "message_id": message_id,
        "sender_uhid": sender,
        "content": content,
        "sent_at_ms": sent_at_ms,
    }))
    .unwrap();
    let mut p = MeshPacket::new(PacketType::ChannelMessage, sender.to_string());
    p.destination_uhid = "*".to_string();
    p.ttl = ttl;
    p.payload = body;
    p
}

// ─── Subscriptions ──────────────────────────────────────

#[test]
fn subscribe_and_unsubscribe_tracks_subscriptions() {
    let (svc, _) = new_svc();
    svc.subscribe("res-floor-3");
    svc.subscribe("society-x");
    let mut subs = svc.get_subscriptions();
    subs.sort();
    assert_eq!(subs, vec!["res-floor-3".to_string(), "society-x".to_string()]);

    svc.unsubscribe("society-x");
    assert_eq!(svc.get_subscriptions(), vec!["res-floor-3".to_string()]);
}

#[test]
fn subscribe_empty_channel_is_ignored() {
    let (svc, _) = new_svc();
    svc.subscribe("");
    assert!(svc.get_subscriptions().is_empty());
}

// ─── Publish ────────────────────────────────────────────

#[tokio::test]
async fn publish_broadcasts_channel_message() {
    let (svc, sender) = new_svc_for("aether:alice:01");

    let delivered = svc.publish("res-floor-3", "meeting at 6").await;
    assert_eq!(delivered, 0); // no peers wired

    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1);
    let pkt = &bcasts[0];
    assert_eq!(pkt.packet_type, PacketType::ChannelMessage);
    assert_eq!(pkt.destination_uhid, "*");
    assert_eq!(pkt.ttl, DEFAULT_TTL);
    assert_eq!(pkt.source_uhid, "aether:alice:01");

    let body: serde_json::Value = serde_json::from_slice(&pkt.payload).unwrap();
    assert_eq!(body["channel_id"], json!("res-floor-3"));
    assert_eq!(body["content"], json!("meeting at 6"));
    assert_eq!(body["sender_uhid"], json!("aether:alice:01"));
}

#[tokio::test]
async fn publish_returns_delivered_peer_count() {
    let (svc, sender) = new_svc();
    // FakeMeshSender.broadcast returns the connected-peer count.
    sender.add_peer(PeerInfo::new("aether:peer:aa".into(), Vec::new()));
    sender.add_peer(PeerInfo::new("aether:peer:bb".into(), Vec::new()));

    let delivered = svc.publish("res-floor-3", "hi").await;
    assert_eq!(delivered, 2);
}

#[tokio::test]
async fn publish_rejects_empty_channel() {
    let (svc, sender) = new_svc();
    let delivered = svc.publish("", "hi").await;
    assert_eq!(delivered, 0);
    assert!(sender.broadcasts().is_empty());
}

#[tokio::test]
async fn publish_seeds_dedup_with_own_id() {
    // The publisher's own message flooding back must be dropped (dedup seeded on publish).
    let (svc, sender) = new_svc_for("aether:alice:01");
    svc.subscribe("res-floor-3");
    let mut events = svc.subscribe_received();

    svc.publish("res-floor-3", "meeting at 6").await;
    let own = sender.broadcasts()[0].clone();

    // Feed our own broadcast back into handle — must be a de-duped no-op.
    let mut echoed = own.clone();
    assert!(!svc.handle(&mut echoed).await);
    assert!(events.try_recv().is_err(), "own message must never surface");
}

// ─── Handle ─────────────────────────────────────────────

#[tokio::test]
async fn handle_subscribed_channel_raises_event() {
    let (svc, _) = new_svc();
    svc.subscribe("res-floor-3");
    let mut events = svc.subscribe_received();

    let ok = svc
        .handle(&mut channel_packet(
            "res-floor-3",
            Uuid::new_v4(),
            "aether:bob:02",
            "hello floor",
            1_700_000_000_000,
            7,
        ))
        .await;

    assert!(ok);
    let got = events.try_recv().expect("expected a channel-message-received event");
    assert_eq!(got.channel_id, "res-floor-3");
    assert_eq!(got.content, "hello floor");
    assert_eq!(got.sender_uhid, "aether:bob:02");
}

#[tokio::test]
async fn handle_unsubscribed_channel_no_event_but_processed() {
    let (svc, _) = new_svc();
    let mut events = svc.subscribe_received();

    let ok = svc
        .handle(&mut channel_packet(
            "society-x",
            Uuid::new_v4(),
            "aether:bob:02",
            "hi",
            1,
            7,
        ))
        .await;

    assert!(ok); // processed + relayed
    assert!(events.try_recv().is_err()); // but not surfaced — we aren't subscribed
}

#[tokio::test]
async fn handle_own_message_not_surfaced() {
    // A message whose author is this node must not surface, even if subscribed.
    let (svc, _) = new_svc_for("aether:local:01");
    svc.subscribe("res-floor-3");
    let mut events = svc.subscribe_received();

    let ok = svc
        .handle(&mut channel_packet(
            "res-floor-3",
            Uuid::new_v4(),
            "aether:local:01",
            "mine",
            1,
            7,
        ))
        .await;

    assert!(ok);
    assert!(events.try_recv().is_err(), "own message must never surface");
}

#[tokio::test]
async fn handle_duplicate_message_id_returns_false() {
    let (svc, _) = new_svc();
    svc.subscribe("res-floor-3");
    let mut events = svc.subscribe_received();
    let id = Uuid::new_v4();

    assert!(
        svc.handle(&mut channel_packet("res-floor-3", id, "aether:bob:02", "one", 1, 7))
            .await
    );
    assert!(
        !svc.handle(&mut channel_packet("res-floor-3", id, "aether:bob:02", "one", 1, 7))
            .await
    );

    assert!(events.try_recv().is_ok(), "first copy surfaces");
    assert!(events.try_recv().is_err(), "duplicate must not surface again");
}

#[tokio::test]
async fn handle_wrong_packet_type_returns_false() {
    let (svc, _) = new_svc();
    let mut pkt = channel_packet("res-floor-3", Uuid::new_v4(), "aether:bob:02", "x", 1, 7);
    pkt.packet_type = PacketType::Data;
    assert!(!svc.handle(&mut pkt).await);
}

#[tokio::test]
async fn handle_malformed_payload_returns_false() {
    let (svc, _) = new_svc();
    let mut pkt = channel_packet("res-floor-3", Uuid::new_v4(), "aether:bob:02", "x", 1, 7);
    pkt.payload = b"not json".to_vec();
    assert!(!svc.handle(&mut pkt).await);
}

// ─── Relay ──────────────────────────────────────────────

#[tokio::test]
async fn handle_relays_when_ttl_allows() {
    let (svc, relay_sender) = new_svc_for("aether:relay:09"); // not subscribed — pure relay
    svc.handle(&mut channel_packet(
        "res-floor-3",
        Uuid::new_v4(),
        "aether:bob:02",
        "hop",
        1,
        5,
    ))
    .await;

    let bcasts = relay_sender.broadcasts();
    assert_eq!(bcasts.len(), 1);
    assert_eq!(bcasts[0].packet_type, PacketType::ChannelMessage);
    assert_eq!(bcasts[0].ttl, 4);
}

#[tokio::test]
async fn handle_does_not_relay_when_ttl_exhausted() {
    let (svc, relay_sender) = new_svc_for("aether:relay:09");
    svc.handle(&mut channel_packet(
        "res-floor-3",
        Uuid::new_v4(),
        "aether:bob:02",
        "last hop",
        1,
        1,
    ))
    .await;
    assert!(relay_sender.broadcasts().is_empty());
}

#[tokio::test]
async fn handle_does_not_relay_own_message() {
    // Own message must never be re-flooded, even with TTL to spare.
    let (svc, sender) = new_svc_for("aether:local:01");
    svc.handle(&mut channel_packet(
        "res-floor-3",
        Uuid::new_v4(),
        "aether:local:01",
        "mine",
        1,
        5,
    ))
    .await;
    assert!(sender.broadcasts().is_empty());
}
