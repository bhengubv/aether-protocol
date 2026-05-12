// SPDX-License-Identifier: MIT

//! Reputation gossip — propagates signed reputation-score deltas over the mesh.
//!
//! [`ReputationGossipService`] builds, signs, and broadcasts
//! [`ReputationUpdatePayload`] packets (type 52) and processes inbound ones,
//! applying a reporter-trust-weighted delta to the local
//! [`NodeReputationService`].

use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use serde::{Deserialize, Serialize};

use crate::reputation::NodeReputationService;

const PACKET_TYPE_REPUTATION_UPDATE: u8 = 52;
const FRESHNESS_WINDOW_MS: i64 = 5 * 60 * 1000; // 5 minutes

// ── Public payload / packet types ────────────────────────────────────────────

/// JSON payload carried by a `ReputationUpdate` (packet type 52) gossip packet.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReputationUpdatePayload {
    pub reporter_uhid: String,
    pub target_uhid: String,
    pub score_delta: f64,
    pub timestamp_ms: i64,
    pub reason: String,
}

/// Minimal packet struct for gossip — only the fields needed by this service.
///
/// The full [`crate::protocol::MeshPacket`] carries extras (UUID, priority,
/// protocol-version …) that gossip does not need and that would complicate the
/// injected-sender/signer interface.  Hosts adapt between the two at the
/// boundary.
#[derive(Debug, Clone)]
pub struct GossipPacket {
    pub packet_type: u8,
    pub source_uhid: String,
    pub destination_uhid: String,
    pub ttl: u8,
    pub payload: Vec<u8>,
    pub timestamp_ms: i64,
    pub signature: Vec<u8>,
    pub packet_nonce: Vec<u8>,
}

// ── Injectable traits ─────────────────────────────────────────────────────────

/// Synchronous mesh-send abstraction injected into [`ReputationGossipService`].
///
/// This is intentionally distinct from the async
/// [`crate::routing::MeshSender`] — gossip signing and broadcasting are
/// lightweight, synchronous operations that do not benefit from async overhead.
pub trait GossipSender: Send + Sync {
    /// The UHID this node uses as the packet source.
    fn local_uhid(&self) -> &str;
    /// Broadcast a gossip packet to all directly-connected peers.
    /// Returns the fan-out count (number of peers reached).
    fn broadcast(&self, packet: GossipPacket) -> usize;
}

/// Synchronous packet-signing abstraction injected into
/// [`ReputationGossipService`].
pub trait PacketSigner: Send + Sync {
    /// Populate `packet.signature` (and optionally `packet.packet_nonce`).
    fn sign(&self, packet: &mut GossipPacket);
    /// Return `true` iff the packet's signature is valid for `sender_public_key`.
    fn verify(&self, packet: &GossipPacket, sender_public_key: &[u8]) -> bool;
}

// ── Service ───────────────────────────────────────────────────────────────────

pub struct ReputationGossipService<S: GossipSender, V: PacketSigner> {
    sender: S,
    signer: V,
    reputation: Arc<NodeReputationService>,
}

impl<S: GossipSender, V: PacketSigner> ReputationGossipService<S, V> {
    pub fn new(sender: S, signer: V, reputation: Arc<NodeReputationService>) -> Self {
        Self { sender, signer, reputation }
    }

    /// Build, sign, and broadcast a `ReputationUpdate` gossip packet.
    ///
    /// `score_delta` is clamped to [-1.0, 1.0] before serialisation.
    pub fn broadcast_reputation_update(&self, target_uhid: &str, score_delta: f64, reason: &str) {
        let clamped = score_delta.clamp(-1.0, 1.0);
        let now_ms = now_ms();
        let payload = ReputationUpdatePayload {
            reporter_uhid: self.sender.local_uhid().to_string(),
            target_uhid: target_uhid.to_string(),
            score_delta: clamped,
            timestamp_ms: now_ms,
            reason: reason.to_string(),
        };
        let payload_bytes = serde_json::to_vec(&payload).unwrap_or_default();
        let mut packet = GossipPacket {
            packet_type: PACKET_TYPE_REPUTATION_UPDATE,
            source_uhid: self.sender.local_uhid().to_string(),
            destination_uhid: "*".to_string(),
            ttl: 3,
            payload: payload_bytes,
            timestamp_ms: now_ms,
            signature: Vec::new(),
            packet_nonce: Vec::new(),
        };
        self.signer.sign(&mut packet);
        self.sender.broadcast(packet);
    }

    /// Process an inbound gossip packet.
    ///
    /// Returns `true` if the packet was accepted and the weighted delta applied
    /// to the local reputation service.  Returns `false` (and applies nothing)
    /// if any guard check fails.
    pub fn handle_gossip_packet(
        &self,
        packet: &GossipPacket,
        sender_public_key: &[u8],
    ) -> bool {
        // 1. Type guard
        if packet.packet_type != PACKET_TYPE_REPUTATION_UPDATE {
            return false;
        }
        // 2. Signature verification
        if !self.signer.verify(packet, sender_public_key) {
            return false;
        }
        // 3. Deserialise payload
        let payload: ReputationUpdatePayload = match serde_json::from_slice(&packet.payload) {
            Ok(p) => p,
            Err(_) => return false,
        };
        // 4. Freshness check (absolute age must be within FRESHNESS_WINDOW_MS)
        let now = now_ms();
        if (now - payload.timestamp_ms).abs() > FRESHNESS_WINDOW_MS {
            return false;
        }
        // 5. Non-empty UHID fields
        if payload.reporter_uhid.is_empty() || payload.target_uhid.is_empty() {
            return false;
        }
        // 6. Own-echo guard — ignore our own gossip bouncing back
        if payload.reporter_uhid == self.sender.local_uhid() {
            return false;
        }
        // 7. Weight by reporter's own reputation (unknown defaults to 1.0)
        let reporter_score = self.reputation.get_reputation_score(&payload.reporter_uhid);
        // 8. Apply weighted delta
        let clamped = payload.score_delta.clamp(-1.0, 1.0);
        let effective = clamped * reporter_score;
        self.reputation.apply_weighted_delta(&payload.target_uhid, effective);
        true
    }
}

// ── Internal helpers ──────────────────────────────────────────────────────────

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    // ── Fake implementations ──────────────────────────────────────────────────

    struct FakeSender {
        local: String,
        broadcasts: Mutex<Vec<GossipPacket>>,
    }

    impl FakeSender {
        fn new(local: &str) -> Self {
            Self {
                local: local.to_string(),
                broadcasts: Mutex::new(Vec::new()),
            }
        }

        fn broadcast_count(&self) -> usize {
            self.broadcasts.lock().unwrap().len()
        }

        fn last_packet_payload(&self) -> ReputationUpdatePayload {
            let guard = self.broadcasts.lock().unwrap();
            let last = guard.last().expect("no broadcasts recorded");
            serde_json::from_slice(&last.payload).expect("invalid payload JSON")
        }
    }

    impl GossipSender for FakeSender {
        fn local_uhid(&self) -> &str {
            &self.local
        }
        fn broadcast(&self, packet: GossipPacket) -> usize {
            self.broadcasts.lock().unwrap().push(packet);
            1
        }
    }

    struct FakeSigner {
        verify_ok: bool,
    }

    impl PacketSigner for FakeSigner {
        fn sign(&self, packet: &mut GossipPacket) {
            packet.packet_nonce = vec![0u8; 8];
            packet.signature = vec![0u8; 64];
        }
        fn verify(&self, _: &GossipPacket, _: &[u8]) -> bool {
            self.verify_ok
        }
    }

    // ── Helper: build a valid inbound gossip packet ───────────────────────────

    fn make_valid_packet(
        reporter: &str,
        target: &str,
        delta: f64,
        ts_ms: i64,
    ) -> GossipPacket {
        let payload = ReputationUpdatePayload {
            reporter_uhid: reporter.to_string(),
            target_uhid: target.to_string(),
            score_delta: delta,
            timestamp_ms: ts_ms,
            reason: "test".to_string(),
        };
        let payload_bytes = serde_json::to_vec(&payload).unwrap();
        GossipPacket {
            packet_type: PACKET_TYPE_REPUTATION_UPDATE,
            source_uhid: reporter.to_string(),
            destination_uhid: "*".to_string(),
            ttl: 3,
            payload: payload_bytes,
            timestamp_ms: ts_ms,
            signature: vec![0u8; 64],
            packet_nonce: vec![0u8; 8],
        }
    }

    const EPS: f64 = 1e-9;

    fn assert_near(label: &str, got: f64, expected: f64) {
        assert!(
            (got - expected).abs() < EPS,
            "{label}: expected {expected:.6}, got {got:.6}"
        );
    }

    // ── Test 1 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_broadcast_sends_one_packet() {
        let sender = FakeSender::new("local-node");
        let svc = ReputationGossipService::new(
            sender,
            FakeSigner { verify_ok: true },
            Arc::new(NodeReputationService::new()),
        );
        svc.broadcast_reputation_update("target-node", -0.10, "bad routing");
        assert_eq!(svc.sender.broadcast_count(), 1);
    }

    // ── Test 2 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_broadcast_payload_fields() {
        let sender = FakeSender::new("local-node");
        let svc = ReputationGossipService::new(
            sender,
            FakeSigner { verify_ok: true },
            Arc::new(NodeReputationService::new()),
        );
        svc.broadcast_reputation_update("target-node", -0.20, "sig failure observed");

        let payload = svc.sender.last_packet_payload();
        assert_eq!(payload.reporter_uhid, "local-node");
        assert_eq!(payload.target_uhid, "target-node");
        assert_near("score_delta", payload.score_delta, -0.20);
        assert_eq!(payload.reason, "sig failure observed");
        // timestamp should be recent (within 5 seconds)
        let age = (now_ms() - payload.timestamp_ms).abs();
        assert!(age < 5_000, "timestamp too stale: {age}ms");

        // Verify packet-level fields
        let guard = svc.sender.broadcasts.lock().unwrap();
        let pkt = guard.last().unwrap();
        assert_eq!(pkt.packet_type, PACKET_TYPE_REPUTATION_UPDATE);
        assert_eq!(pkt.destination_uhid, "*");
        assert_eq!(pkt.ttl, 3);
        assert_eq!(pkt.source_uhid, "local-node");
        assert_eq!(pkt.signature.len(), 64);
        assert_eq!(pkt.packet_nonce.len(), 8);
    }

    // ── Test 3 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_broadcast_clamps_delta_above_one() {
        let sender = FakeSender::new("local-node");
        let svc = ReputationGossipService::new(
            sender,
            FakeSigner { verify_ok: true },
            Arc::new(NodeReputationService::new()),
        );
        svc.broadcast_reputation_update("target-node", 5.0, "extreme positive");

        let payload = svc.sender.last_packet_payload();
        assert_near("clamped delta", payload.score_delta, 1.0);
    }

    // ── Test 4 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_broadcast_clamps_delta_below_minus_one() {
        let sender = FakeSender::new("local-node");
        let svc = ReputationGossipService::new(
            sender,
            FakeSigner { verify_ok: true },
            Arc::new(NodeReputationService::new()),
        );
        svc.broadcast_reputation_update("target-node", -9.0, "extreme negative");

        let payload = svc.sender.last_packet_payload();
        assert_near("clamped delta", payload.score_delta, -1.0);
    }

    // ── Test 5 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_handle_invalid_signature() {
        let rep = Arc::new(NodeReputationService::new());
        let svc = ReputationGossipService::new(
            FakeSender::new("local-node"),
            FakeSigner { verify_ok: false }, // ← verification always fails
            Arc::clone(&rep),
        );
        let pkt = make_valid_packet("reporter-1", "target-1", -0.10, now_ms());
        assert!(!svc.handle_gossip_packet(&pkt, &[]));
        // Score must be untouched
        assert_near("score unchanged", rep.get_reputation_score("target-1"), 1.0);
    }

    // ── Test 6 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_handle_wrong_type() {
        let rep = Arc::new(NodeReputationService::new());
        let svc = ReputationGossipService::new(
            FakeSender::new("local-node"),
            FakeSigner { verify_ok: true },
            Arc::clone(&rep),
        );
        let mut pkt = make_valid_packet("reporter-1", "target-1", -0.10, now_ms());
        pkt.packet_type = 99;
        assert!(!svc.handle_gossip_packet(&pkt, &[]));
        assert_near("score unchanged", rep.get_reputation_score("target-1"), 1.0);
    }

    // ── Test 7 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_handle_stale_timestamp() {
        let rep = Arc::new(NodeReputationService::new());
        let svc = ReputationGossipService::new(
            FakeSender::new("local-node"),
            FakeSigner { verify_ok: true },
            Arc::clone(&rep),
        );
        // 6 minutes ago — outside the 5-minute freshness window
        let stale_ts = now_ms() - 6 * 60 * 1000;
        let pkt = make_valid_packet("reporter-1", "target-1", -0.10, stale_ts);
        assert!(!svc.handle_gossip_packet(&pkt, &[]));
        assert_near("score unchanged", rep.get_reputation_score("target-1"), 1.0);
    }

    // ── Test 8 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_handle_missing_reporter_uhid() {
        let rep = Arc::new(NodeReputationService::new());
        let svc = ReputationGossipService::new(
            FakeSender::new("local-node"),
            FakeSigner { verify_ok: true },
            Arc::clone(&rep),
        );
        // Empty reporter_uhid
        let pkt = make_valid_packet("", "target-1", -0.10, now_ms());
        assert!(!svc.handle_gossip_packet(&pkt, &[]));
        assert_near("score unchanged", rep.get_reputation_score("target-1"), 1.0);
    }

    // ── Test 9 ─────────────────────────────────────────────────────────────────
    #[test]
    fn test_handle_own_gossip() {
        let rep = Arc::new(NodeReputationService::new());
        let svc = ReputationGossipService::new(
            FakeSender::new("local-node"),
            FakeSigner { verify_ok: true },
            Arc::clone(&rep),
        );
        // reporter_uhid == local_uhid → own-echo guard
        let pkt = make_valid_packet("local-node", "target-1", -0.10, now_ms());
        assert!(!svc.handle_gossip_packet(&pkt, &[]));
        assert_near("score unchanged", rep.get_reputation_score("target-1"), 1.0);
    }

    // ── Test 10 ────────────────────────────────────────────────────────────────
    // Reporter is unknown (defaults to score 1.0), target starts at 1.0.
    // Effective delta = -0.20 × 1.0 = -0.20  →  target = 0.80
    #[test]
    fn test_handle_unknown_reporter_full_delta() {
        let rep = Arc::new(NodeReputationService::new());
        let svc = ReputationGossipService::new(
            FakeSender::new("local-node"),
            FakeSigner { verify_ok: true },
            Arc::clone(&rep),
        );
        let pkt = make_valid_packet("unknown-reporter", "target-10", -0.20, now_ms());
        assert!(svc.handle_gossip_packet(&pkt, &[]));
        // unknown-reporter has score 1.0 → full -0.20 applied
        assert_near("target score", rep.get_reputation_score("target-10"), 0.80);
    }

    // ── Test 11 ────────────────────────────────────────────────────────────────
    // Reporter has been degraded by 10× RREQ flood → score 0.50 (1.0 - 10×0.05).
    // Gossip delta = -0.20 → effective = -0.20 × 0.50 = -0.10 → target 0.90.
    #[test]
    fn test_handle_degraded_reporter_weighted_delta() {
        let rep = Arc::new(NodeReputationService::new());
        // Degrade reporter to 0.50 via 10 RREQ flood records
        for _ in 0..10 {
            rep.record_rreq_flood_attempt("degraded-reporter"); // each -0.05
        }
        assert_near("reporter score", rep.get_reputation_score("degraded-reporter"), 0.50);

        let svc = ReputationGossipService::new(
            FakeSender::new("local-node"),
            FakeSigner { verify_ok: true },
            Arc::clone(&rep),
        );
        let pkt = make_valid_packet("degraded-reporter", "target-11", -0.20, now_ms());
        assert!(svc.handle_gossip_packet(&pkt, &[]));
        // -0.20 × 0.50 = -0.10 → target goes from 1.0 to 0.90
        assert_near("target score", rep.get_reputation_score("target-11"), 0.90);
    }

    // ── Test 12 ────────────────────────────────────────────────────────────────
    // Target pre-degraded to 0.80 (one sig failure).  Positive gossip +0.10 × 1.0 → 0.90.
    #[test]
    fn test_handle_positive_delta_improves_target() {
        let rep = Arc::new(NodeReputationService::new());
        // Pre-degrade target
        rep.record_signature_failure("target-12"); // → 0.80
        assert_near("target before", rep.get_reputation_score("target-12"), 0.80);

        let svc = ReputationGossipService::new(
            FakeSender::new("local-node"),
            FakeSigner { verify_ok: true },
            Arc::clone(&rep),
        );
        // Reporter unknown → 1.0; delta +0.10 → effective +0.10
        let pkt = make_valid_packet("trusted-reporter", "target-12", 0.10, now_ms());
        assert!(svc.handle_gossip_packet(&pkt, &[]));
        // 0.80 + 0.10 = 0.90
        assert_near("target score after", rep.get_reputation_score("target-12"), 0.90);
    }
}
