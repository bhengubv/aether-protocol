// SPDX-License-Identifier: MIT
//! Integration tests for the routing service. Mirrors the C# canonical suite.

#[path = "common.rs"]
mod common;

use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use aethernet_protocol::{
    constants::{DEFAULT_TTL, ROUTE_EXPIRY_SECONDS},
    models::RouteEntry,
    protocol::PacketType,
    routing::{
        verifier::AcceptAllRouteReplyVerifier, InMemoryRouteStore, RouteReplyVerifier,
        RouteStore, RoutingService,
    },
};
use common::{new_rreq, new_rrep, FakeMeshSender};

const LOCAL: &str = "local-uhid";

fn now_secs() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs()
}

async fn new_svc() -> (RoutingService, Arc<FakeMeshSender>, Arc<InMemoryRouteStore>) {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    let svc = RoutingService::with_dependencies(
        sender.clone(),
        store.clone(),
        Arc::new(AcceptAllRouteReplyVerifier),
        Arc::new(aethernet_protocol::extensibility::NoopIncentiveProvider),
    );
    (svc, sender, store)
}

// ─── HandleRouteRequest ─────────────────────────────────

#[tokio::test]
async fn handle_rreq_drops_duplicate_by_id() {
    let (svc, sender, _) = new_svc().await;
    let rreq = new_rreq("alice", "bob", DEFAULT_TTL);
    svc.handle_route_request(&mut rreq.clone()).await;
    sender.clear();
    svc.handle_route_request(&mut rreq.clone()).await;
    assert!(sender.broadcasts().is_empty());
    assert!(sender.unicasts().is_empty());
}

#[tokio::test]
async fn handle_rreq_ignores_self_originated() {
    let (svc, sender, store) = new_svc().await;
    let rreq = new_rreq(LOCAL, "bob", DEFAULT_TTL);
    svc.handle_route_request(&mut rreq.clone()).await;
    assert!(sender.broadcasts().is_empty());
    assert!(sender.unicasts().is_empty());
    assert!(store.get_all().await.is_empty());
}

#[tokio::test]
async fn handle_rreq_installs_reverse_route() {
    let (svc, _, store) = new_svc().await;
    let rreq = new_rreq("alice", "bob", DEFAULT_TTL);
    svc.handle_route_request(&mut rreq.clone()).await;
    let r = store.get("alice").await;
    let r = r.expect("reverse route");
    assert_eq!(r.next_hop_uhid, "alice");
    assert!(r.hop_count >= 1);
    assert!(!r.is_expired());
}

#[tokio::test]
async fn handle_rreq_as_destination_sends_rrep_back() {
    let (svc, sender, _) = new_svc().await;
    let rreq = new_rreq("alice", LOCAL, DEFAULT_TTL);
    svc.handle_route_request(&mut rreq.clone()).await;

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    let rec = &unicasts[0];
    assert_eq!(rec.packet.packet_type, PacketType::RouteReply);
    assert_eq!(rec.packet.source_uhid, LOCAL);
    assert_eq!(rec.packet.destination_uhid, "alice");
    assert_eq!(rec.next_hop_uhid, "alice");
}

#[tokio::test]
async fn handle_rreq_with_cached_route_replies_on_behalf() {
    let (svc, sender, store) = new_svc().await;
    let entry = RouteEntry {
        destination_uhid: "carol".into(),
        next_hop_uhid: "carol".into(),
        hop_count: 1,
        quality_score: 50,
        expires_at: now_secs() + ROUTE_EXPIRY_SECONDS,
        last_updated: now_secs(),
    };
    store.save(entry.clone()).await;
    let _ = svc.find_route("carol").await; // populate cache
    sender.clear();

    let rreq = new_rreq("alice", "carol", DEFAULT_TTL);
    svc.handle_route_request(&mut rreq.clone()).await;

    let mut rrep = None;
    for u in sender.unicasts() {
        if u.packet.packet_type == PacketType::RouteReply {
            rrep = Some(u.packet.clone());
            break;
        }
    }
    if rrep.is_none() {
        for b in sender.broadcasts() {
            if b.packet_type == PacketType::RouteReply {
                rrep = Some(b);
                break;
            }
        }
    }
    let rrep = rrep.expect("expected an RREP");
    assert_eq!(rrep.source_uhid, "carol");
}

#[tokio::test]
async fn handle_rreq_forwards_when_ttl_allows() {
    let (svc, sender, _) = new_svc().await;
    let rreq = new_rreq("alice", "carol", 5);
    svc.handle_route_request(&mut rreq.clone()).await;
    let bcasts = sender.broadcasts();
    assert_eq!(bcasts.len(), 1);
    assert_eq!(bcasts[0].ttl, 4);
}

#[tokio::test]
async fn handle_rreq_drops_when_ttl_exhausted() {
    let (svc, sender, _) = new_svc().await;
    let rreq = new_rreq("alice", "carol", 1);
    svc.handle_route_request(&mut rreq.clone()).await;
    assert!(sender.broadcasts().is_empty());
    assert!(sender.unicasts().is_empty());
}

// ─── HandleRouteReply ───────────────────────────────────

#[tokio::test]
async fn handle_rrep_installs_forward_route() {
    let (svc, _, store) = new_svc().await;
    let rrep = new_rrep("carol", LOCAL, DEFAULT_TTL);
    svc.handle_route_reply(&mut rrep.clone()).await;
    let r = store.get("carol").await.expect("forward route");
    assert_eq!(r.next_hop_uhid, "carol");
}

#[tokio::test]
async fn handle_rrep_rejects_when_verifier_fails() {
    struct Reject;
    #[async_trait::async_trait]
    impl RouteReplyVerifier for Reject {
        async fn verify(&self, _: &aethernet_protocol::protocol::MeshPacket) -> bool {
            false
        }
    }
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    let svc = RoutingService::with_dependencies(
        sender.clone(),
        store.clone(),
        Arc::new(Reject),
        Arc::new(aethernet_protocol::extensibility::NoopIncentiveProvider),
    );
    let rrep = new_rrep("carol", LOCAL, DEFAULT_TTL);
    svc.handle_route_reply(&mut rrep.clone()).await;
    assert!(store.get("carol").await.is_none());
}

#[tokio::test]
async fn handle_rrep_forwards_toward_original_requester() {
    let (svc, sender, store) = new_svc().await;
    let reverse = RouteEntry {
        destination_uhid: "alice".into(),
        next_hop_uhid: "bob".into(),
        hop_count: 2,
        quality_score: 50,
        expires_at: now_secs() + ROUTE_EXPIRY_SECONDS,
        last_updated: now_secs(),
    };
    store.save(reverse.clone()).await;
    let _ = svc.find_route("alice").await;
    sender.clear();

    let rrep = new_rrep("carol", "alice", 4);
    svc.handle_route_reply(&mut rrep.clone()).await;

    let fwd = sender.unicasts().into_iter().find(|u| {
        u.packet.packet_type == PacketType::RouteReply && u.next_hop_uhid == "bob"
    });
    let fwd = fwd.expect("forwarded RREP");
    assert_eq!(fwd.packet.ttl, 3);
}

// ─── FindRoute / Prune ──────────────────────────────────

#[tokio::test]
async fn find_route_returns_cached_without_broadcasting() {
    let (svc, sender, store) = new_svc().await;
    let entry = RouteEntry {
        destination_uhid: "bob".into(),
        next_hop_uhid: "bob".into(),
        hop_count: 1,
        quality_score: 50,
        expires_at: now_secs() + ROUTE_EXPIRY_SECONDS,
        last_updated: now_secs(),
    };
    store.save(entry.clone()).await;
    let r = svc.find_route("bob").await;
    assert!(r.is_some());
    assert!(sender.broadcasts().is_empty());
}

#[tokio::test]
async fn find_route_returns_none_when_no_peers() {
    let (svc, _, _) = new_svc().await;
    let r = svc.find_route("bob").await;
    assert!(r.is_none());
}

#[tokio::test]
async fn prune_removes_expired_routes() {
    let (svc, _, store) = new_svc().await;
    let stale = RouteEntry {
        destination_uhid: "stale".into(),
        next_hop_uhid: "stale".into(),
        hop_count: 1,
        quality_score: 50,
        expires_at: now_secs().saturating_sub(60),
        last_updated: now_secs().saturating_sub(60),
    };
    let fresh = RouteEntry {
        destination_uhid: "fresh".into(),
        next_hop_uhid: "fresh".into(),
        hop_count: 1,
        quality_score: 50,
        expires_at: now_secs() + ROUTE_EXPIRY_SECONDS,
        last_updated: now_secs(),
    };
    store.save(stale).await;
    store.save(fresh).await;
    let _ = svc.find_route("fresh").await;

    svc.prune().await;

    assert!(store.get("stale").await.is_none());
    assert!(store.get("fresh").await.is_some());
    let _ = Duration::from_secs(0); // satisfy unused import
}
