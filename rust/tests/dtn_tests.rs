// SPDX-License-Identifier: MIT
//! Integration tests for the DTN service.

#[path = "common.rs"]
mod common;

use serde_json::json;
use std::sync::Arc;
use uuid::Uuid;

use aethernet_protocol::{
    constants::{DTN_BUNDLE_TTL_HOURS, DTN_MAX_BUNDLES_PER_NODE},
    dtn::{BundleStore, DtnService, InMemoryBundleStore},
    extensibility::{NoopBackendClient, NoopIncentiveProvider},
    models::{BundlePriority, BundleStatus, DtnBundle, PeerInfo},
    protocol::{MeshPacket, PacketType},
};
use common::FakeMeshSender;

const LOCAL: &str = "local";

fn now_secs() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_secs()
}

async fn new_svc() -> (DtnService, Arc<FakeMeshSender>, Arc<InMemoryBundleStore>) {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryBundleStore::new());
    let svc = DtnService::with_dependencies(
        sender.clone(),
        store.clone(),
        Arc::new(aethernet_protocol::dtn::GeohashEpidemicStrategy),
        Arc::new(NoopIncentiveProvider),
        Arc::new(NoopBackendClient),
    );
    (svc, sender, store)
}

fn build_bundle_packet(source: &str, bundle: &DtnBundle) -> MeshPacket {
    let body = json!({
        "id": bundle.id,
        "sender_uhid": bundle.sender_uhid,
        "recipient_uhid": bundle.recipient_uhid,
        "encrypted_payload": bundle.encrypted_payload,
        "priority": bundle.priority.as_u8(),
        "status": bundle.status.as_u8(),
        "copy_count": bundle.copy_count,
        "max_copies": bundle.max_copies,
        "sender_geohash": bundle.sender_geohash,
        "recipient_last_geohash": bundle.recipient_last_geohash,
        "hop_count": bundle.hop_count,
        "created_at_ms": (bundle.created_at as i64) * 1000,
        "expires_at_ms": (bundle.expires_at as i64) * 1000,
    });
    let mut p = MeshPacket::new(PacketType::DtnBundle, source.to_string());
    p.destination_uhid = bundle.recipient_uhid.clone();
    p.payload = serde_json::to_vec(&body).unwrap();
    p
}

// ─── CreateBundle ───────────────────────────────────────

#[tokio::test]
async fn create_bundle_persists_and_attempts_delivery() {
    let (svc, _, store) = new_svc().await;
    let bundle = svc
        .create_bundle("recipient", vec![1, 2, 3], BundlePriority::Normal, None)
        .await;
    assert_eq!(bundle.recipient_uhid, "recipient");
    assert_eq!(bundle.status, BundleStatus::Pending);
    assert_eq!(store.get_active().await.len(), 1);
}

#[tokio::test]
async fn create_bundle_with_direct_peer_delivers_immediately() {
    let (svc, sender, _) = new_svc().await;
    sender.add_peer(PeerInfo {
        uhid: "recipient".into(),
        public_key: vec![],
        last_seen: std::time::SystemTime::now(),
        hop_count: 0,
        reliability_score: 50,
        capabilities: 128, // DtnCarrier
        geohash: None,
        is_blocked: false,
    });
    let bundle = svc
        .create_bundle("recipient", vec![1, 2, 3], BundlePriority::Normal, None)
        .await;
    assert_eq!(bundle.status, BundleStatus::Delivered);
    let hit = sender
        .unicasts()
        .iter()
        .any(|u| u.next_hop_uhid == "recipient" && u.packet.packet_type == PacketType::DtnBundle);
    assert!(hit);
}

// ─── HandleAsync — DtnBundle ────────────────────────────

#[tokio::test]
async fn handle_as_recipient_marks_delivered_and_sends_receipt() {
    let (svc, sender, store) = new_svc().await;
    let bundle = DtnBundle::new(
        "alice".to_string(),
        LOCAL.to_string(),
        vec![9],
        BundlePriority::Normal,
        DTN_BUNDLE_TTL_HOURS,
    );
    let pkt = build_bundle_packet("alice", &bundle);
    svc.handle(&pkt).await;

    let stored = store.get(&bundle.id).await.expect("stored");
    assert_eq!(stored.status, BundleStatus::Delivered);
    assert!(sender.unicasts().iter().any(|u| u.packet.packet_type == PacketType::DtnDeliveryReceipt
        && u.next_hop_uhid == "alice"));
}

#[tokio::test]
async fn handle_not_recipient_with_capacity_accepts_custody() {
    let (svc, sender, store) = new_svc().await;
    let bundle = DtnBundle::new(
        "alice".to_string(),
        "bob".to_string(),
        vec![1],
        BundlePriority::Normal,
        DTN_BUNDLE_TTL_HOURS,
    );
    let pkt = build_bundle_packet("alice", &bundle);
    svc.handle(&pkt).await;

    let stored = store.get(&bundle.id).await.expect("stored");
    assert_eq!(stored.status, BundleStatus::InCustody);
    assert_eq!(stored.hop_count, 1);
    assert!(sender.unicasts().iter().any(|u| u.packet.packet_type == PacketType::DtnCustodyAck
        && u.next_hop_uhid == "alice"));
}

#[tokio::test]
async fn handle_at_capacity_refuses_custody() {
    let (svc, sender, store) = new_svc().await;
    for _ in 0..DTN_MAX_BUNDLES_PER_NODE {
        let mut fill = DtnBundle::new(
            "x".to_string(),
            "y".to_string(),
            vec![],
            BundlePriority::Normal,
            DTN_BUNDLE_TTL_HOURS,
        );
        fill.status = BundleStatus::InCustody;
        store.save(fill).await;
    }
    sender.clear();

    let bundle = DtnBundle::new(
        "alice".to_string(),
        "bob".to_string(),
        vec![],
        BundlePriority::Normal,
        DTN_BUNDLE_TTL_HOURS,
    );
    let pkt = build_bundle_packet("alice", &bundle);
    svc.handle(&pkt).await;

    let ack = sender
        .unicasts()
        .into_iter()
        .find(|u| u.packet.packet_type == PacketType::DtnCustodyAck)
        .expect("ack");
    let body: serde_json::Value = serde_json::from_slice(&ack.packet.payload).unwrap();
    assert_eq!(body["accepted"], false);
}

// ─── DtnCustodyAck ───────────────────────────────────────

#[tokio::test]
async fn handle_positive_custody_ack_increments_copy_count() {
    let (svc, _, store) = new_svc().await;
    let bundle = svc
        .create_bundle("recipient", vec![1], BundlePriority::Normal, None)
        .await;
    let initial = bundle.copy_count;

    let body = serde_json::to_vec(&json!({
        "bundle_id": bundle.id,
        "accepted": true,
    }))
    .unwrap();
    let mut pkt = MeshPacket::new(PacketType::DtnCustodyAck, "carrier".to_string());
    pkt.destination_uhid = LOCAL.to_string();
    pkt.payload = body;
    svc.handle(&pkt).await;

    let stored = store.get(&bundle.id).await.expect("stored");
    assert_eq!(stored.copy_count, initial + 1);
}

#[tokio::test]
async fn handle_negative_custody_ack_does_not_increment() {
    let (svc, _, store) = new_svc().await;
    let bundle = svc
        .create_bundle("recipient", vec![1], BundlePriority::Normal, None)
        .await;
    let initial = bundle.copy_count;

    let body = serde_json::to_vec(&json!({
        "bundle_id": bundle.id,
        "accepted": false,
    }))
    .unwrap();
    let mut pkt = MeshPacket::new(PacketType::DtnCustodyAck, "carrier".to_string());
    pkt.destination_uhid = LOCAL.to_string();
    pkt.payload = body;
    svc.handle(&pkt).await;

    let stored = store.get(&bundle.id).await.expect("stored");
    assert_eq!(stored.copy_count, initial);
}

// ─── DtnDeliveryReceipt ─────────────────────────────────

#[tokio::test]
async fn handle_delivery_receipt_marks_bundle_delivered() {
    let (svc, _, store) = new_svc().await;
    let bundle = svc
        .create_bundle("recipient", vec![1], BundlePriority::Normal, None)
        .await;
    let body = serde_json::to_vec(&json!({
        "bundle_id": bundle.id,
        "recipient_uhid": "recipient",
        "total_hops": 3,
        "total_custody_transfers": 2,
        "delivered_at_ms": 0,
    }))
    .unwrap();
    let mut pkt = MeshPacket::new(PacketType::DtnDeliveryReceipt, "recipient".to_string());
    pkt.destination_uhid = LOCAL.to_string();
    pkt.payload = body;
    svc.handle(&pkt).await;

    let stored = store.get(&bundle.id).await.expect("stored");
    assert_eq!(stored.status, BundleStatus::Delivered);
}

// ─── ExpireStale ────────────────────────────────────────

#[tokio::test]
async fn expire_stale_flips_status_for_expired_bundles() {
    let (svc, _, store) = new_svc().await;
    let mut expired = DtnBundle::new(
        "a".to_string(),
        "b".to_string(),
        vec![],
        BundlePriority::Normal,
        1,
    );
    expired.status = BundleStatus::Pending;
    expired.expires_at = now_secs().saturating_sub(60);
    store.save(expired).await;

    let mut fresh = DtnBundle::new(
        "a".to_string(),
        "b".to_string(),
        vec![],
        BundlePriority::Normal,
        1,
    );
    fresh.status = BundleStatus::Pending;
    let fresh_id = fresh.id;
    store.save(fresh).await;

    let n = svc.expire_stale().await;
    assert_eq!(n, 1);
    assert_eq!(
        store.get(&fresh_id).await.unwrap().status,
        BundleStatus::Pending
    );
    let _ = Uuid::nil();
}

// ─── OnBundleReceived (v1.2.0, Issue #59) ─────────────────────────────────

#[tokio::test]
async fn inbound_bundle_addressed_to_local_fires_bundle_received_event() {
    use aethernet_protocol::dtn::DtnBundleReceivedEvent;
    let sender = FakeMeshSender::new("recipient");
    let store = Arc::new(InMemoryBundleStore::new());
    let svc = DtnService::with_dependencies(
        sender.clone(),
        store.clone(),
        Arc::new(aethernet_protocol::dtn::GeohashEpidemicStrategy),
        Arc::new(NoopIncentiveProvider),
        Arc::new(NoopBackendClient),
    );

    let mut rx = svc.subscribe_bundle_received();

    let bundle = DtnBundle {
        id: Uuid::new_v4(),
        sender_uhid: "remote-sender".to_string(),
        recipient_uhid: "recipient".to_string(),
        encrypted_payload: vec![1, 2, 3, 4],
        priority: BundlePriority::High,
        status: BundleStatus::Pending,
        copy_count: 1,
        max_copies: 16,
        sender_geohash: None,
        recipient_last_geohash: None,
        hop_count: 2,
        created_at: now_secs(),
        expires_at: now_secs() + 72 * 3600,
    };
    let pkt = build_bundle_packet("carrier", &bundle);
    svc.handle(&pkt).await;

    let evt: DtnBundleReceivedEvent = tokio::time::timeout(
        std::time::Duration::from_millis(500),
        rx.recv(),
    )
    .await
    .expect("event channel did not deliver within 500ms")
    .expect("broadcast channel closed");

    assert_eq!(evt.bundle_id, bundle.id);
    assert_eq!(evt.sender_uhid, "remote-sender");
    assert_eq!(evt.recipient_uhid, "recipient");
    assert_eq!(evt.encrypted_payload, vec![1, 2, 3, 4]);
    assert_eq!(evt.priority, BundlePriority::High);
    assert_eq!(evt.hop_count, 2);
}

#[tokio::test]
async fn inbound_bundle_for_other_node_does_not_fire_bundle_received_event() {
    let sender = FakeMeshSender::new("carrier");
    sender.add_peer(PeerInfo {
        uhid: "peer-z".into(),
        public_key: vec![],
        last_seen: std::time::SystemTime::now(),
        hop_count: 0,
        reliability_score: 50,
        capabilities: 128, // DtnCarrier
        geohash: None,
        is_blocked: false,
    });
    let store = Arc::new(InMemoryBundleStore::new());
    let svc = DtnService::with_dependencies(
        sender.clone(),
        store.clone(),
        Arc::new(aethernet_protocol::dtn::GeohashEpidemicStrategy),
        Arc::new(NoopIncentiveProvider),
        Arc::new(NoopBackendClient),
    );

    let mut rx = svc.subscribe_bundle_received();

    let bundle = DtnBundle {
        id: Uuid::new_v4(),
        sender_uhid: "remote-sender".to_string(),
        recipient_uhid: "someone-else".to_string(),
        encrypted_payload: vec![0xff],
        priority: BundlePriority::Normal,
        status: BundleStatus::Pending,
        copy_count: 1,
        max_copies: 16,
        sender_geohash: None,
        recipient_last_geohash: None,
        hop_count: 0,
        created_at: now_secs(),
        expires_at: now_secs() + 72 * 3600,
    };
    let pkt = build_bundle_packet("remote-sender", &bundle);
    svc.handle(&pkt).await;

    let result = tokio::time::timeout(
        std::time::Duration::from_millis(100),
        rx.recv(),
    )
    .await;
    assert!(
        result.is_err(),
        "bundle_received event must NOT fire for relay-custody path"
    );
}
