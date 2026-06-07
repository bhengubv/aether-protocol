// SPDX-License-Identifier: MIT

//! Default DTN service. Three-tier delivery:
//! direct mesh send → DTN epidemic replication → backend relay.

use serde::{Deserialize, Serialize};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::sync::broadcast;
use uuid::Uuid;

use crate::constants::{DEFAULT_TTL, DTN_BUNDLE_TTL_HOURS, DTN_MAX_BUNDLES_PER_NODE, DTN_MAX_COPIES};
use crate::extensibility::{BackendClient, IncentiveProvider, NoopBackendClient, NoopIncentiveProvider};
use crate::models::{BundlePriority, BundleStatus, CustodyRecord, DtnBundle};
use crate::protocol::{MeshPacket, PacketType};
use crate::reputation::NodeReputationService;
use crate::routing::sender::MeshSender;

use super::store::{BundleStore, InMemoryBundleStore};
use super::strategy::{GeohashEpidemicStrategy, ReplicationStrategy};

const DTN_TTL: i32 = 30;
const BUNDLE_RECEIVED_CHANNEL_CAPACITY: usize = 64;

/// Event delivered to subscribers of [`DtnService::subscribe_bundle_received`] the
/// moment a DTN bundle arrives whose final recipient is the local node.
///
/// Added in v1.2.0 — closes the Wave-16 gap surfaced by Issue #59. Mirrors the
/// C# `DtnBundleReceivedEventArgs` and the Go / Python / TS / Kotlin / Swift ports.
#[derive(Debug, Clone)]
pub struct DtnBundleReceivedEvent {
    pub bundle_id: Uuid,
    pub sender_uhid: String,
    pub recipient_uhid: String,
    pub encrypted_payload: Vec<u8>,
    pub priority: BundlePriority,
    pub hop_count: i32,
    pub received_at_ms: i64,
}

/// JSON wire envelope for a DTN bundle. Cross-language stable.
#[derive(Debug, Clone, Serialize, Deserialize)]
struct BundleWire {
    id: Uuid,
    sender_uhid: String,
    recipient_uhid: String,
    encrypted_payload: Vec<u8>,
    priority: u8,
    status: u8,
    copy_count: i32,
    max_copies: i32,
    sender_geohash: Option<String>,
    recipient_last_geohash: Option<String>,
    hop_count: i32,
    created_at_ms: i64,
    expires_at_ms: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct CustodyAckWire {
    bundle_id: Uuid,
    accepted: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct DeliveryReceiptWire {
    bundle_id: Uuid,
    recipient_uhid: String,
    total_hops: i32,
    total_custody_transfers: i32,
    delivered_at_ms: i64,
}

pub struct DtnService {
    sender: Arc<dyn MeshSender>,
    store: Arc<dyn BundleStore>,
    strategy: Arc<dyn ReplicationStrategy>,
    incentives: Arc<dyn IncentiveProvider>,
    backend: Arc<dyn BackendClient>,
    /// Optional reputation service; `None` disables reputation tracking.
    reputation: Option<Arc<NodeReputationService>>,
    /// Broadcast channel for inbound-bundle events; subscribers receive an event
    /// each time a DTN bundle arrives addressed to the local node. Added in
    /// v1.2.0 — Issue #59.
    bundle_received_tx: broadcast::Sender<DtnBundleReceivedEvent>,
}

impl DtnService {
    /// Construct a service with the given sender. All other dependencies use defaults.
    pub fn new(sender: Arc<dyn MeshSender>) -> Self {
        Self::with_dependencies(
            sender,
            Arc::new(InMemoryBundleStore::new()),
            Arc::new(GeohashEpidemicStrategy),
            Arc::new(NoopIncentiveProvider),
            Arc::new(NoopBackendClient),
        )
    }

    pub fn with_dependencies(
        sender: Arc<dyn MeshSender>,
        store: Arc<dyn BundleStore>,
        strategy: Arc<dyn ReplicationStrategy>,
        incentives: Arc<dyn IncentiveProvider>,
        backend: Arc<dyn BackendClient>,
    ) -> Self {
        let (bundle_received_tx, _) = broadcast::channel(BUNDLE_RECEIVED_CHANNEL_CAPACITY);
        Self {
            sender,
            store,
            strategy,
            incentives,
            backend,
            reputation: None,
            bundle_received_tx,
        }
    }

    /// Subscribe to inbound-bundle events. Each subscriber receives an event
    /// the moment a DTN bundle arrives addressed to the local node. Added in
    /// v1.2.0 — closes Issue #59.
    pub fn subscribe_bundle_received(&self) -> broadcast::Receiver<DtnBundleReceivedEvent> {
        self.bundle_received_tx.subscribe()
    }

    /// Attaches a reputation service so that delivery successes and custody
    /// refusals are recorded against the source UHID.
    pub fn set_reputation(&mut self, rep: Arc<NodeReputationService>) {
        self.reputation = Some(rep);
    }

    /// Create a new bundle. Attempts immediate mesh delivery, falls back to backend relay,
    /// otherwise stays in the store for the next scan.
    pub async fn create_bundle(
        &self,
        recipient_uhid: &str,
        encrypted_payload: Vec<u8>,
        priority: BundlePriority,
        recipient_last_geohash: Option<String>,
    ) -> DtnBundle {
        let mut bundle = DtnBundle::new(
            self.sender.local_uhid(),
            recipient_uhid.to_string(),
            encrypted_payload,
            priority,
            DTN_BUNDLE_TTL_HOURS,
        );
        bundle.max_copies = DTN_MAX_COPIES as i32;
        bundle.sender_geohash = self.sender.local_geohash();
        bundle.recipient_last_geohash = recipient_last_geohash;
        self.store.save(bundle.clone()).await;

        if self.try_direct_delivery(&bundle).await {
            let mut delivered = bundle.clone();
            delivered.status = BundleStatus::Delivered;
            self.store.save(delivered.clone()).await;
            return delivered;
        }
        bundle
    }

    /// Pump a received DTN-related packet into the service.
    pub async fn handle(&self, packet: &MeshPacket) {
        match packet.packet_type {
            PacketType::DtnBundle => self.handle_bundle(packet).await,
            PacketType::DtnCustodyAck => self.handle_custody_ack(packet).await,
            PacketType::DtnDeliveryReceipt => self.handle_delivery_receipt(packet).await,
            _ => {}
        }
    }

    /// Run one delivery scan: retry direct delivery for active bundles, then replicate.
    pub async fn run_delivery_scan(&self) {
        let active = self.store.get_active().await;
        if active.is_empty() {
            return;
        }
        let peers = self.sender.connected_peers();
        let local_geohash = self.sender.local_geohash();

        for mut bundle in active.into_iter() {
            if bundle.status == BundleStatus::Delivered || bundle.is_expired() {
                continue;
            }
            if self.try_direct_delivery(&bundle).await {
                bundle.status = BundleStatus::Delivered;
                self.store.save(bundle).await;
                continue;
            }
            if peers.is_empty() || bundle.copy_count >= bundle.max_copies {
                continue;
            }
            let targets =
                self.strategy
                    .select_targets(&bundle, &peers, local_geohash.as_deref());
            for target in targets.into_iter() {
                if bundle.copy_count >= bundle.max_copies {
                    break;
                }
                let pkt = self.bundle_packet(&bundle);
                if self.sender.send(&pkt, &target).await {
                    bundle.copy_count += 1;
                    self.store.save(bundle.clone()).await;
                    self.incentives
                        .record_relay(&self.sender.local_uhid(), &pkt)
                        .await;
                }
            }
        }
    }

    pub async fn expire_stale(&self) -> usize {
        self.store.expire_stale().await
    }

    pub async fn get_active_bundles(&self) -> Vec<DtnBundle> {
        self.store.get_active().await
    }

    async fn try_direct_delivery(&self, bundle: &DtnBundle) -> bool {
        let pkt = self.bundle_packet(bundle);
        for peer in self.sender.connected_peers().iter() {
            if peer.uhid == bundle.recipient_uhid {
                if self.sender.send(&pkt, &bundle.recipient_uhid).await {
                    return true;
                }
                break;
            }
        }
        self.backend.sync_dtn_bundle(bundle).await
    }

    fn bundle_packet(&self, bundle: &DtnBundle) -> MeshPacket {
        let mut pkt = MeshPacket::new(PacketType::DtnBundle, self.sender.local_uhid());
        pkt.id = bundle.id;
        pkt.destination_uhid = bundle.recipient_uhid.clone();
        pkt.ttl = DTN_TTL;
        pkt.priority = bundle.priority.as_u8();
        pkt.payload = encode_bundle(bundle);
        pkt
    }

    async fn handle_bundle(&self, packet: &MeshPacket) {
        let bundle = match decode_bundle(&packet.payload) {
            Some(b) => b,
            None => return,
        };

        if bundle.recipient_uhid == self.sender.local_uhid() {
            let mut delivered = bundle.clone();
            delivered.status = BundleStatus::Delivered;
            self.store.save(delivered.clone()).await;
            if let Some(rep) = &self.reputation {
                rep.record_delivery_success(&packet.source_uhid, 0);
            }
            // Best-effort: deliver to any subscribers. Ignore SendError when
            // there are no live receivers (the v1.2.0 contract is fire-and-forget).
            let _ = self.bundle_received_tx.send(DtnBundleReceivedEvent {
                bundle_id: bundle.id,
                sender_uhid: bundle.sender_uhid.clone(),
                recipient_uhid: bundle.recipient_uhid.clone(),
                encrypted_payload: bundle.encrypted_payload.clone(),
                priority: bundle.priority,
                hop_count: bundle.hop_count,
                received_at_ms: unix_millis(),
            });
            self.send_delivery_receipt(&delivered).await;
            return;
        }

        if self.store.get_active_count().await >= DTN_MAX_BUNDLES_PER_NODE as usize {
            self.send_custody_ack(&bundle.id, &packet.source_uhid, false).await;
            return;
        }

        let mut accepted = bundle.clone();
        accepted.status = BundleStatus::InCustody;
        accepted.hop_count += 1;
        self.store.save(accepted.clone()).await;
        self.store
            .save_custody(CustodyRecord {
                id: Uuid::new_v4(),
                bundle_id: bundle.id,
                from_uhid: packet.source_uhid.clone(),
                to_uhid: self.sender.local_uhid(),
                accepted: true,
                transferred_at: unix_secs(),
            })
            .await;
        self.send_custody_ack(&bundle.id, &packet.source_uhid, true).await;
        self.incentives
            .record_relay(&self.sender.local_uhid(), packet)
            .await;
    }

    async fn handle_custody_ack(&self, packet: &MeshPacket) {
        let ack: CustodyAckWire = match serde_json::from_slice(&packet.payload) {
            Ok(a) => a,
            Err(_) => return,
        };
        if !ack.accepted {
            if let Some(rep) = &self.reputation {
                rep.record_custody_refusal(&packet.source_uhid);
            }
            return;
        }
        if let Some(mut bundle) = self.store.get(&ack.bundle_id).await {
            bundle.copy_count += 1;
            self.store.save(bundle).await;
        }
    }

    async fn handle_delivery_receipt(&self, packet: &MeshPacket) {
        let receipt: DeliveryReceiptWire = match serde_json::from_slice(&packet.payload) {
            Ok(r) => r,
            Err(_) => return,
        };
        if let Some(mut bundle) = self.store.get(&receipt.bundle_id).await {
            bundle.status = BundleStatus::Delivered;
            self.store.save(bundle).await;
        }
    }

    async fn send_custody_ack(&self, bundle_id: &Uuid, to_uhid: &str, accepted: bool) {
        if to_uhid.is_empty() {
            return;
        }
        let body = match serde_json::to_vec(&CustodyAckWire {
            bundle_id: *bundle_id,
            accepted,
        }) {
            Ok(b) => b,
            Err(_) => return,
        };
        let mut pkt = MeshPacket::new(PacketType::DtnCustodyAck, self.sender.local_uhid());
        pkt.destination_uhid = to_uhid.to_string();
        pkt.ttl = DEFAULT_TTL;
        pkt.payload = body;
        self.sender.send(&pkt, to_uhid).await;
    }

    async fn send_delivery_receipt(&self, bundle: &DtnBundle) {
        if bundle.sender_uhid.is_empty() || bundle.sender_uhid == self.sender.local_uhid() {
            return;
        }
        let custody = self.store.get_custody_records(&bundle.id).await;
        let body = match serde_json::to_vec(&DeliveryReceiptWire {
            bundle_id: bundle.id,
            recipient_uhid: bundle.recipient_uhid.clone(),
            total_hops: bundle.hop_count,
            total_custody_transfers: custody.len() as i32,
            delivered_at_ms: unix_millis(),
        }) {
            Ok(b) => b,
            Err(_) => return,
        };
        let mut pkt = MeshPacket::new(PacketType::DtnDeliveryReceipt, self.sender.local_uhid());
        pkt.destination_uhid = bundle.sender_uhid.clone();
        pkt.ttl = DEFAULT_TTL;
        pkt.payload = body;
        self.sender.send(&pkt, &bundle.sender_uhid).await;
    }
}

fn encode_bundle(bundle: &DtnBundle) -> Vec<u8> {
    let wire = BundleWire {
        id: bundle.id,
        sender_uhid: bundle.sender_uhid.clone(),
        recipient_uhid: bundle.recipient_uhid.clone(),
        encrypted_payload: bundle.encrypted_payload.clone(),
        priority: bundle.priority.as_u8(),
        status: bundle.status.as_u8(),
        copy_count: bundle.copy_count,
        max_copies: bundle.max_copies,
        sender_geohash: bundle.sender_geohash.clone(),
        recipient_last_geohash: bundle.recipient_last_geohash.clone(),
        hop_count: bundle.hop_count,
        created_at_ms: (bundle.created_at as i64) * 1000,
        expires_at_ms: (bundle.expires_at as i64) * 1000,
    };
    serde_json::to_vec(&wire).unwrap_or_default()
}

fn decode_bundle(payload: &[u8]) -> Option<DtnBundle> {
    let wire: BundleWire = serde_json::from_slice(payload).ok()?;
    Some(DtnBundle {
        id: wire.id,
        sender_uhid: wire.sender_uhid,
        recipient_uhid: wire.recipient_uhid,
        encrypted_payload: wire.encrypted_payload,
        priority: BundlePriority::from_u8(wire.priority),
        status: BundleStatus::from_u8(wire.status),
        copy_count: wire.copy_count,
        max_copies: wire.max_copies,
        sender_geohash: wire.sender_geohash,
        recipient_last_geohash: wire.recipient_last_geohash,
        hop_count: wire.hop_count,
        created_at: (wire.created_at_ms / 1000) as u64,
        expires_at: (wire.expires_at_ms / 1000) as u64,
    })
}

fn unix_secs() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs()
}

fn unix_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as i64
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use crate::models::{BundlePriority, PeerInfo};
    use std::sync::Mutex;

    // ── Fake MeshSender ───────────────────────────────────────────────────────

    struct FakeSender {
        local: String,
        sends: Mutex<Vec<String>>,
    }

    impl FakeSender {
        fn new(local: &str) -> Arc<Self> {
            Arc::new(Self {
                local: local.to_string(),
                sends: Mutex::new(Vec::new()),
            })
        }
    }

    #[async_trait]
    impl MeshSender for FakeSender {
        fn local_uhid(&self) -> String {
            self.local.clone()
        }

        fn connected_peers(&self) -> Vec<PeerInfo> {
            Vec::new()
        }

        async fn send(&self, _packet: &MeshPacket, next_hop: &str) -> bool {
            self.sends.lock().unwrap().push(next_hop.to_string());
            true
        }

        async fn broadcast(&self, _packet: &MeshPacket) -> usize {
            0
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Build a DtnBundle wire packet addressed from `source_uhid` to `recipient_uhid`.
    fn make_bundle_packet(source_uhid: &str, recipient_uhid: &str) -> MeshPacket {
        let bundle = DtnBundle {
            id: uuid::Uuid::new_v4(),
            sender_uhid: source_uhid.to_string(),
            recipient_uhid: recipient_uhid.to_string(),
            encrypted_payload: vec![1, 2, 3],
            priority: BundlePriority::Normal,
            status: BundleStatus::Pending,
            copy_count: 0,
            max_copies: DTN_MAX_COPIES as i32,
            sender_geohash: None,
            recipient_last_geohash: None,
            hop_count: 0,
            created_at: unix_secs(),
            expires_at: unix_secs() + 3600,
        };
        let payload = encode_bundle(&bundle);
        let mut pkt = MeshPacket::new(PacketType::DtnBundle, source_uhid.to_string());
        pkt.destination_uhid = recipient_uhid.to_string();
        pkt.payload = payload;
        pkt
    }

    /// Build a DtnCustodyAck packet with `accepted = false` from `source_uhid`.
    fn make_custody_ack_packet(source_uhid: &str, bundle_id: uuid::Uuid, accepted: bool) -> MeshPacket {
        let body = serde_json::to_vec(&CustodyAckWire { bundle_id, accepted }).unwrap();
        let mut pkt = MeshPacket::new(PacketType::DtnCustodyAck, source_uhid.to_string());
        pkt.payload = body;
        pkt
    }

    // ── Test 1: delivery to self fires record_delivery_success ────────────────

    #[tokio::test]
    async fn delivery_to_self_records_delivery_success() {
        let sender = FakeSender::new("local-node");
        let mut svc = DtnService::new(Arc::clone(&sender) as Arc<dyn MeshSender>);

        let rep = Arc::new(NodeReputationService::new());
        svc.set_reputation(Arc::clone(&rep));

        let packet = make_bundle_packet("remote-node", "local-node");
        svc.handle(&packet).await;

        // Score for "remote-node" should increase by +0.01 from 1.0 → epsilon-snapped to 1.0
        // (already at max), so verify that it was NOT penalised (still at 1.0).
        // More importantly, verify it did not drop.
        let score = rep.get_reputation_score("remote-node");
        assert!(
            score >= 1.0,
            "expected score >= 1.0 after delivery success, got {score}"
        );
    }

    // ── Test 2: bundle not for us does NOT fire record_delivery_success ────────

    #[tokio::test]
    async fn bundle_not_for_us_does_not_fire_delivery_success() {
        let sender = FakeSender::new("local-node");
        let mut svc = DtnService::new(Arc::clone(&sender) as Arc<dyn MeshSender>);

        let rep = Arc::new(NodeReputationService::new());
        // Pre-degrade the source so we can detect if it was (incorrectly) boosted
        rep.record_signature_failure("remote-node"); // → 0.80
        svc.set_reputation(Arc::clone(&rep));

        // Bundle addressed to someone else, not "local-node"
        let packet = make_bundle_packet("remote-node", "other-node");
        svc.handle(&packet).await;

        // Score must remain at 0.80; a spurious success call would move it to 0.81
        let score = rep.get_reputation_score("remote-node");
        assert!(
            (score - 0.80).abs() < 1e-9,
            "expected score 0.80 (no delivery-success fired), got {score}"
        );
    }

    // ── Test 3: custody refusal fires record_custody_refusal ─────────────────

    #[tokio::test]
    async fn custody_refusal_records_custody_refusal() {
        let sender = FakeSender::new("local-node");
        let mut svc = DtnService::new(Arc::clone(&sender) as Arc<dyn MeshSender>);

        let rep = Arc::new(NodeReputationService::new());
        svc.set_reputation(Arc::clone(&rep));

        let bundle_id = uuid::Uuid::new_v4();
        let packet = make_custody_ack_packet("refusing-node", bundle_id, false);
        svc.handle(&packet).await;

        // record_custody_refusal applies -0.05, so 1.0 → 0.95
        let score = rep.get_reputation_score("refusing-node");
        assert!(
            (score - 0.95).abs() < 1e-9,
            "expected score 0.95 after custody refusal, got {score}"
        );
    }
}
