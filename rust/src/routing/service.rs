// SPDX-License-Identifier: MIT

//! AODV-inspired reactive routing service.
//!
//! Lifecycle:
//!   * Callers invoke [`RoutingService::find_route`] when they need a route to a
//!     destination. Cached routes return immediately; otherwise an RREQ is broadcast
//!     and the call awaits the matching RREP (subject to `ROUTE_TIMEOUT_MS`).
//!   * Hosts pump received RREQ / RREP packets through
//!     [`RoutingService::handle_route_request`] / [`RoutingService::handle_route_reply`].
//!   * Hosts call [`RoutingService::prune`] periodically.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use tokio::sync::{oneshot, Mutex};
use tokio::time::timeout;
use uuid::Uuid;

use crate::constants::{DEFAULT_TTL, ROUTE_EXPIRY_SECONDS, ROUTE_TIMEOUT_MS};
use crate::extensibility::{IncentiveProvider, NoopIncentiveProvider};
use crate::models::RouteEntry;
use crate::protocol::{MeshPacket, PacketType};
use crate::reputation::NodeReputationService;

use super::sender::MeshSender;
use super::store::{InMemoryRouteStore, RouteStore};
use super::verifier::{RejectAllRouteReplyVerifier, RouteReplyVerifier};

/// AODV-inspired reactive routing service.
pub struct RoutingService {
    sender: Arc<dyn MeshSender>,
    store: Arc<dyn RouteStore>,
    verifier: Arc<dyn RouteReplyVerifier>,
    incentives: Arc<dyn IncentiveProvider>,
    /// Optional reputation service; `None` disables reputation tracking.
    reputation: Option<Arc<NodeReputationService>>,

    state: Mutex<State>,
}

struct State {
    cache: HashMap<String, RouteEntry>,
    pending: HashMap<String, oneshot::Sender<Option<RouteEntry>>>,
    seen_rreqs: HashSet<Uuid>,
    loaded: bool,
}

impl RoutingService {
    /// Construct a service with the given sender. All other dependencies use defaults.
    ///
    /// Fail-closed: with no verifier supplied the service uses [`RejectAllRouteReplyVerifier`],
    /// so every RREP is REJECTED rather than trusting unverified route replies (which would let
    /// any forwarder hijack routes). A host wires a real signature verifier (e.g.
    /// [`Ed25519RouteReplyVerifier`](super::verifier::Ed25519RouteReplyVerifier)) via
    /// [`with_dependencies`](Self::with_dependencies) to permit legitimate, signed RREPs.
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        Self::with_dependencies(
            sender,
            Arc::new(InMemoryRouteStore::new()),
            Arc::new(RejectAllRouteReplyVerifier),
            Arc::new(NoopIncentiveProvider),
        )
    }

    pub fn with_dependencies(
        sender: Arc<dyn MeshSender>,
        store: Arc<dyn RouteStore>,
        verifier: Arc<dyn RouteReplyVerifier>,
        incentives: Arc<dyn IncentiveProvider>,
    ) -> Self {
        Self {
            sender,
            store,
            verifier,
            incentives,
            reputation: None,
            state: Mutex::new(State {
                cache: HashMap::new(),
                pending: HashMap::new(),
                seen_rreqs: HashSet::new(),
                loaded: false,
            }),
        }
    }

    /// Attaches a reputation service so that RREQ flood attempts are recorded.
    pub fn with_reputation(mut self, reputation: Arc<NodeReputationService>) -> Self {
        self.reputation = Some(reputation);
        self
    }

    pub async fn find_route(&self, destination_uhid: &str) -> Option<RouteEntry> {
        if destination_uhid.is_empty() {
            return None;
        }
        self.ensure_loaded().await;

        {
            let state = self.state.lock().await;
            if let Some(cached) = state.cache.get(destination_uhid) {
                if !route_expired(cached) {
                    return Some(cached.clone());
                }
            }
        }

        if let Some(stored) = self.store.get(destination_uhid).await {
            if !route_expired(&stored) {
                let mut state = self.state.lock().await;
                state.cache.insert(stored.destination_uhid.clone(), stored.clone());
                return Some(stored);
            }
        }

        self.discover(destination_uhid).await
    }

    pub async fn get_cached_route(&self, destination_uhid: &str) -> Option<RouteEntry> {
        if destination_uhid.is_empty() {
            return None;
        }
        let state = self.state.lock().await;
        state.cache.get(destination_uhid).filter(|r| !route_expired(r)).cloned()
    }

    pub async fn get_all_routes(&self) -> Vec<RouteEntry> {
        let state = self.state.lock().await;
        state.cache.values().filter(|r| !route_expired(r)).cloned().collect()
    }

    pub async fn handle_route_request(&self, rreq: &mut MeshPacket) {
        if rreq.packet_type != PacketType::RouteRequest {
            return;
        }
        {
            let mut state = self.state.lock().await;
            if !state.seen_rreqs.insert(rreq.id) {
                // Duplicate RREQ packet — record as flood attempt.
                let source = rreq.source_uhid.clone();
                drop(state);
                if let Some(rep) = &self.reputation {
                    rep.record_rreq_flood_attempt(&source);
                }
                return;
            }
        }

        let local = self.sender.local_uhid();
        if rreq.source_uhid.is_empty() || rreq.source_uhid == local {
            return;
        }

        let now = unix_secs();
        let hop_count = (DEFAULT_TTL - rreq.ttl + 1).max(1) as u32;
        let reverse = RouteEntry {
            destination_uhid: rreq.source_uhid.clone(),
            next_hop_uhid: rreq.source_uhid.clone(),
            hop_count,
            quality_score: 50,
            expires_at: now + ROUTE_EXPIRY_SECONDS,
            last_updated: now,
        };
        {
            let mut state = self.state.lock().await;
            state.cache.insert(reverse.destination_uhid.clone(), reverse.clone());
        }
        self.store.save(reverse.clone()).await;

        if rreq.destination_uhid == local {
            self.send_rrep(&local, rreq).await;
            return;
        }

        let known = {
            let state = self.state.lock().await;
            state.cache.get(&rreq.destination_uhid).cloned()
        };
        if let Some(k) = known {
            if !route_expired(&k) {
                self.send_rrep(&rreq.destination_uhid, rreq).await;
                return;
            }
        }

        if rreq.ttl > 1 {
            rreq.ttl -= 1;
            self.sender.broadcast(rreq).await;
            self.incentives.record_relay(&local, rreq).await;
        }
    }

    pub async fn handle_route_reply(&self, rrep: &mut MeshPacket) {
        if rrep.packet_type != PacketType::RouteReply {
            return;
        }
        if !self.verifier.verify(rrep).await {
            return;
        }

        let local = self.sender.local_uhid();
        if rrep.source_uhid.is_empty() || rrep.source_uhid == local {
            return;
        }

        let now = unix_secs();
        let hop_count = (DEFAULT_TTL - rrep.ttl + 1).max(1) as u32;
        let forward = RouteEntry {
            destination_uhid: rrep.source_uhid.clone(),
            next_hop_uhid: rrep.source_uhid.clone(),
            hop_count,
            quality_score: 50,
            expires_at: now + ROUTE_EXPIRY_SECONDS,
            last_updated: now,
        };
        let pending = {
            let mut state = self.state.lock().await;
            state.cache.insert(forward.destination_uhid.clone(), forward.clone());
            if rrep.destination_uhid == local {
                state.pending.remove(&forward.destination_uhid)
            } else {
                None
            }
        };
        self.store.save(forward.clone()).await;

        if let Some(tx) = pending {
            let _ = tx.send(Some(forward.clone()));
        }

        if rrep.destination_uhid == local || rrep.ttl <= 1 {
            return;
        }

        let next = {
            let state = self.state.lock().await;
            state.cache.get(&rrep.destination_uhid).cloned()
        };
        if let Some(n) = next {
            if !route_expired(&n) {
                rrep.ttl -= 1;
                let delivered = self.sender.send(rrep, &n.next_hop_uhid).await;
                if delivered {
                    self.incentives.record_relay(&local, rrep).await;
                }
            }
        }
    }

    pub async fn prune(&self) {
        let mut state = self.state.lock().await;
        state.cache.retain(|_, r| !route_expired(r));
        if state.seen_rreqs.len() > 10_000 {
            state.seen_rreqs.clear();
        }
        drop(state);
        let _ = self.store.prune_expired().await;
    }

    async fn send_rrep(&self, replied_source: &str, rreq: &MeshPacket) {
        let mut rrep = MeshPacket::new(PacketType::RouteReply, replied_source.to_string());
        rrep.destination_uhid = rreq.source_uhid.clone();
        rrep.ttl = DEFAULT_TTL;
        rrep.payload = rreq.payload.clone();

        let reverse = {
            let state = self.state.lock().await;
            state.cache.get(&rreq.source_uhid).cloned()
        };
        if let Some(r) = reverse {
            if !route_expired(&r) {
                self.sender.send(&rrep, &r.next_hop_uhid).await;
                return;
            }
        }
        self.sender.broadcast(&rrep).await;
    }

    async fn discover(&self, destination_uhid: &str) -> Option<RouteEntry> {
        let (tx, rx) = oneshot::channel::<Option<RouteEntry>>();
        {
            let mut state = self.state.lock().await;
            state.pending.insert(destination_uhid.to_string(), tx);
        }

        let mut rreq = MeshPacket::new(PacketType::RouteRequest, self.sender.local_uhid());
        rreq.destination_uhid = destination_uhid.to_string();
        rreq.ttl = DEFAULT_TTL;

        let fanout = self.sender.broadcast(&rreq).await;
        if fanout == 0 {
            let mut state = self.state.lock().await;
            state.pending.remove(destination_uhid);
            return None;
        }

        match timeout(Duration::from_millis(ROUTE_TIMEOUT_MS), rx).await {
            Ok(Ok(value)) => value,
            _ => {
                let mut state = self.state.lock().await;
                state.pending.remove(destination_uhid);
                None
            }
        }
    }

    async fn ensure_loaded(&self) {
        {
            let state = self.state.lock().await;
            if state.loaded {
                return;
            }
        }
        let routes = self.store.get_all().await;
        let mut state = self.state.lock().await;
        for r in routes.into_iter() {
            if !route_expired(&r) {
                state.cache.insert(r.destination_uhid.clone(), r);
            }
        }
        state.loaded = true;
    }
}

fn unix_secs() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs()
}

fn route_expired(route: &RouteEntry) -> bool {
    unix_secs() >= route.expires_at
}
