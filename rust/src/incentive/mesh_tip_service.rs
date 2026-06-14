// SPDX-License-Identifier: MIT
//
// Default MeshTipService. Sends and receives generic [`PacketType::TipPacket`] (24) packets. Rust
// port of `AetherNet.Security.Services.MeshTipService` (and the Go `incentive.MeshTipService`).
//
// Send path: build a [`TipPacketPayload`] -> sign the payload's canonical bytes with the local
// identity key (real Ed25519) -> serialise as snake_case JSON -> wrap in a [`MeshPacket`] -> sign the
// enclosing packet -> route toward the recipient (unicast over a discovered route, falling back to
// broadcast).
//
// Receive path: deserialise the payload -> best-effort signature check (Ed25519 signature must be
// present and well-formed = 64 bytes) -> hand to the host's [`MeshTipSettlementProvider`] -> relay
// the packet onward toward its addressed recipient. A malformed or unverifiable payload is logged and
// dropped, never returned as an error.
//
// This service is purely a protocol mechanism. It attaches NO value semantics to the amount and
// performs NO settlement — settlement is entirely the host's business, expressed through the injected
// provider. A bare node (default no-op provider) accepts and relays tips but settles nothing.

use uuid::Uuid;

use crate::constants::DEFAULT_TTL;
use crate::protocol::{MeshPacket, PacketType};

use super::TipPacketPayload;

/// Ed25519 signature length in bytes — used for the best-effort inbound check.
const ED25519_SIGNATURE_LENGTH: usize = 64;

/// The minimal mesh transport surface needed by [`MeshTipService`].
///
/// This is kept synchronous and self-contained (mirroring the [`crate::gossip::GossipSender`] idiom)
/// so the tip service has no hard dependency on a specific transport implementation. Hosts wire a
/// thin adapter over their transport.
pub trait MeshSender: Send + Sync {
    /// The UHID of the local node.
    fn local_uhid(&self) -> String;
    /// Deliver `packet` toward `next_hop_uhid`. Returns `true` on success.
    fn send(&self, packet: &MeshPacket, next_hop_uhid: &str) -> bool;
    /// Send `packet` to every directly-connected peer; returns the fan-out count.
    fn broadcast(&self, packet: &MeshPacket) -> usize;
}

/// Handles signing of the enclosing [`MeshPacket`] envelope. The host's implementation populates the
/// signature / nonce / timestamp fields (mirroring `IPacketSigningService.SignPacketAsync`).
pub trait PacketSigner: Send + Sync {
    /// Populate `packet.signature` (and the nonce / timestamp fields) in place.
    fn sign_packet(&self, packet: &mut MeshPacket);
}

/// Signs the tip payload's canonical bytes with the local node's Ed25519 identity key (mirroring
/// `ISignalProtocolService.SignDataAsync`).
pub trait IdentitySigner: Send + Sync {
    /// Produce a 64-byte Ed25519 signature over `data` using the local identity key.
    fn sign_data(&self, data: &[u8]) -> Vec<u8>;
}

/// Resolves a next-hop toward a destination UHID. Returns `Some(next_hop)` when a route is known, or
/// `None` to fall back to broadcast.
pub trait RouteResolver: Send + Sync {
    fn find_next_hop(&self, destination_uhid: &str) -> Option<String>;
}

/// The host's settlement hook — the Rust analog of the C#
/// `IAetherNetIncentiveProvider.SettleMeshTip`. It receives the full signed [`TipPacketPayload`] off
/// the mesh and decides how (if at all) to interpret its value. The default no-op
/// ([`NoopMeshTipSettlementProvider`]) settles nothing.
pub trait MeshTipSettlementProvider: Send + Sync {
    /// Invoked for every inbound, well-formed tip payload. Implementations (e.g. SDPKT / BhenguPay)
    /// wire their wallet settlement here. Returning an error is logged by the caller but never
    /// propagated to the wire — a settlement failure must not break relaying.
    fn settle_mesh_tip(&self, payload: &TipPacketPayload) -> Result<(), String>;
}

/// The default no-op settlement provider — accepts the tip and settles nothing. A bare node carries
/// the tip signal but never moves value.
#[derive(Debug, Default, Clone, Copy)]
pub struct NoopMeshTipSettlementProvider;

impl MeshTipSettlementProvider for NoopMeshTipSettlementProvider {
    fn settle_mesh_tip(&self, _payload: &TipPacketPayload) -> Result<(), String> {
        Ok(())
    }
}

/// Builds, signs, sends, and handles mesh tip packets.
pub struct MeshTipService<S, P, I, R, T>
where
    S: MeshSender,
    P: PacketSigner,
    I: IdentitySigner,
    R: RouteResolver,
    T: MeshTipSettlementProvider,
{
    sender: S,
    signer: P,
    identity: I,
    routing: Option<R>,
    settle: T,
    default_ttl: i32,
}

impl<S, P, I, R, T> MeshTipService<S, P, I, R, T>
where
    S: MeshSender,
    P: PacketSigner,
    I: IdentitySigner,
    R: RouteResolver,
    T: MeshTipSettlementProvider,
{
    /// Constructs a `MeshTipService`. Pass `None` for `routing` to always broadcast.
    pub fn new(sender: S, signer: P, identity: I, routing: Option<R>, settle: T) -> Self {
        Self {
            sender,
            signer,
            identity,
            routing,
            settle,
            default_ttl: DEFAULT_TTL, // ProtocolConstants.DefaultTtl
        }
    }

    /// Builds, signs, and routes a `TipPacket(24)` addressed to `recipient_uhid`. `amount` is the
    /// caller's input verbatim (the invariant decimal string) — the protocol imposes NO policy on it.
    /// It is signed into the payload and carried as-is. Returns the signed [`MeshPacket`] that was
    /// routed onto the mesh.
    pub fn send_tip(
        &self,
        recipient_uhid: &str,
        amount: &str,
        traffic_type: &str,
        reference_id: Option<Uuid>,
        timestamp_unix_ms: i64,
    ) -> Result<MeshPacket, String> {
        let mut payload = TipPacketPayload {
            tipper_uhid: self.sender.local_uhid(),
            recipient_uhid: recipient_uhid.to_string(),
            amount: amount.to_string(),
            traffic_type: traffic_type.to_string(),
            reference_id,
            timestamp_unix_ms,
            signature: None,
        };

        // Sign the payload's canonical bytes with the local identity key (real Ed25519).
        payload.signature = Some(self.identity.sign_data(&payload.build_canonical_data()));

        let body = payload.to_json().map_err(|e| format!("tip payload serialize: {e}"))?;

        let mut packet = MeshPacket::new(PacketType::TipPacket, self.sender.local_uhid());
        packet.destination_uhid = recipient_uhid.to_string();
        packet.ttl = self.default_ttl;
        packet.priority = 0;
        packet.payload = body;

        // Sign the enclosing MeshPacket (fills nonce/timestamp + envelope signature).
        self.signer.sign_packet(&mut packet);

        // Route toward the recipient: unicast over a discovered route, else broadcast.
        if let Some(routing) = self.routing.as_ref() {
            if let Some(next_hop) = routing.find_next_hop(recipient_uhid) {
                self.sender.send(&packet, &next_hop);
                return Ok(packet);
            }
        }
        self.sender.broadcast(&packet);
        Ok(packet)
    }

    /// Processes an inbound `TipPacket(24)` received off the mesh.
    ///
    /// Returns `true` when the payload was accepted and handed to the settlement provider. Returns
    /// `false` when the packet should be silently discarded (wrong type, malformed payload,
    /// missing/malformed signature).
    pub fn handle_tip_packet(&self, packet: &MeshPacket) -> bool {
        if packet.packet_type != PacketType::TipPacket {
            return false;
        }

        // 1. Deserialise the payload. A malformed payload is dropped.
        let payload = match TipPacketPayload::from_json(&packet.payload) {
            Ok(p) => p,
            Err(_) => return false,
        };
        if payload.tipper_uhid.is_empty() || payload.recipient_uhid.is_empty() {
            return false;
        }

        // 2. Best-effort signature check: an Ed25519 signature is exactly 64 bytes. A payload carrying
        //    no signature, or a malformed one, is unverifiable — dropped. The host's settlement
        //    provider is responsible for any stronger, key-bound verification it needs.
        match payload.signature.as_ref() {
            Some(sig) if sig.len() == ED25519_SIGNATURE_LENGTH => {}
            _ => return false,
        }

        // 3. Hand to the host's settlement provider. Default no-op settles nothing. A settlement
        //    error is swallowed but never breaks relaying.
        let _ = self.settle.settle_mesh_tip(&payload);

        // 4. Relay onward toward the addressed recipient if this node is not the destination and the
        //    packet may still be forwarded. The tip is ordinary addressed traffic.
        if packet.destination_uhid != self.sender.local_uhid() && packet.can_forward() {
            if let Some(routing) = self.routing.as_ref() {
                if let Some(next_hop) = routing.find_next_hop(&packet.destination_uhid) {
                    self.sender.send(packet, &next_hop);
                    return true;
                }
            }
            self.sender.broadcast(packet);
        }

        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::security::Ed25519SigningService;
    use std::sync::Mutex;

    /// A `RouteResolver` that never resolves a route (always broadcasts). Used as the concrete `R`
    /// type when a test wants no routing.
    struct NoRoute;
    impl RouteResolver for NoRoute {
        fn find_next_hop(&self, _destination_uhid: &str) -> Option<String> {
            None
        }
    }

    struct FakeSender {
        local: String,
        sent: Mutex<Vec<MeshPacket>>,
        broadcasts: Mutex<Vec<MeshPacket>>,
    }
    impl FakeSender {
        fn new(local: &str) -> Self {
            Self {
                local: local.to_string(),
                sent: Mutex::new(Vec::new()),
                broadcasts: Mutex::new(Vec::new()),
            }
        }
    }
    impl MeshSender for FakeSender {
        fn local_uhid(&self) -> String {
            self.local.clone()
        }
        fn send(&self, packet: &MeshPacket, _next_hop_uhid: &str) -> bool {
            self.sent.lock().unwrap().push(packet.clone());
            true
        }
        fn broadcast(&self, packet: &MeshPacket) -> usize {
            self.broadcasts.lock().unwrap().push(packet.clone());
            1
        }
    }

    struct FakeSigner;
    impl PacketSigner for FakeSigner {
        fn sign_packet(&self, packet: &mut MeshPacket) {
            packet.signature = b"envelope-sig".to_vec();
            packet.packet_nonce = vec![1, 2, 3, 4, 5, 6, 7, 8];
        }
    }

    struct SeedIdentity {
        private_key: Vec<u8>,
    }
    impl IdentitySigner for SeedIdentity {
        fn sign_data(&self, data: &[u8]) -> Vec<u8> {
            Ed25519SigningService::sign(&self.private_key, data).unwrap()
        }
    }

    struct RecordingSettler {
        calls: Mutex<Vec<TipPacketPayload>>,
    }
    impl MeshTipSettlementProvider for RecordingSettler {
        fn settle_mesh_tip(&self, payload: &TipPacketPayload) -> Result<(), String> {
            self.calls.lock().unwrap().push(payload.clone());
            Ok(())
        }
    }

    #[test]
    fn noop_settlement_provider_settles_nothing() {
        let p = TipPacketPayload {
            tipper_uhid: String::new(),
            recipient_uhid: String::new(),
            amount: String::new(),
            traffic_type: String::new(),
            reference_id: None,
            timestamp_unix_ms: 0,
            signature: None,
        };
        assert!(NoopMeshTipSettlementProvider.settle_mesh_tip(&p).is_ok());
    }

    #[test]
    fn send_tip_with_no_route_broadcasts() {
        let (priv_key, _pub) = Ed25519SigningService::generate_keypair();
        let sender = FakeSender::new("aether:tipper:aa");
        let svc = MeshTipService::new(
            sender,
            FakeSigner,
            SeedIdentity { private_key: priv_key },
            None::<NoRoute>,
            NoopMeshTipSettlementProvider,
        );
        let signed = svc
            .send_tip("aether:recipient:bb", "12.50", "message-relay", None, 1_700_000_000_000)
            .unwrap();
        assert_eq!(signed.packet_type, PacketType::TipPacket);
        assert_eq!(svc.sender.broadcasts.lock().unwrap().len(), 1);
        assert_eq!(svc.sender.sent.lock().unwrap().len(), 0);
    }

    #[test]
    fn handle_tip_packet_routes_to_settlement_hook_and_drops_bad_signature() {
        let (priv_key, _pub) = Ed25519SigningService::generate_keypair();
        // Local node is the addressed recipient, so no onward relay happens.
        let sender = FakeSender::new("aether:recipient:bb");
        let settler = RecordingSettler { calls: Mutex::new(Vec::new()) };
        let svc = MeshTipService::new(
            sender,
            FakeSigner,
            SeedIdentity { private_key: priv_key.clone() },
            None::<NoRoute>,
            settler,
        );

        // Well-formed, signed tip payload.
        let mut p = TipPacketPayload {
            tipper_uhid: "aether:tipper:aa".to_string(),
            recipient_uhid: "aether:recipient:bb".to_string(),
            amount: "12.50".to_string(),
            traffic_type: "message-relay".to_string(),
            reference_id: None,
            timestamp_unix_ms: 1_700_000_000_000,
            signature: None,
        };
        p.signature = Some(Ed25519SigningService::sign(&priv_key, &p.build_canonical_data()).unwrap());
        let mut pkt = MeshPacket::new(PacketType::TipPacket, "aether:tipper:aa".to_string());
        pkt.destination_uhid = "aether:recipient:bb".to_string();
        pkt.payload = p.to_json().unwrap();

        assert!(svc.handle_tip_packet(&pkt));
        assert_eq!(svc.settle.calls.lock().unwrap().len(), 1);
        assert_eq!(svc.settle.calls.lock().unwrap()[0].tipper_uhid, "aether:tipper:aa");

        // A malformed signature (wrong length) must be dropped before the hook fires.
        svc.settle.calls.lock().unwrap().clear();
        p.signature = Some(vec![0x00, 0x01, 0x02]);
        let mut bad = MeshPacket::new(PacketType::TipPacket, "aether:tipper:aa".to_string());
        bad.destination_uhid = "aether:recipient:bb".to_string();
        bad.payload = p.to_json().unwrap();
        assert!(!svc.handle_tip_packet(&bad));
        assert_eq!(svc.settle.calls.lock().unwrap().len(), 0);
    }

    #[test]
    fn handle_tip_packet_ignores_wrong_type() {
        let (priv_key, _pub) = Ed25519SigningService::generate_keypair();
        let sender = FakeSender::new("node");
        let svc = MeshTipService::new(
            sender,
            FakeSigner,
            SeedIdentity { private_key: priv_key },
            None::<NoRoute>,
            NoopMeshTipSettlementProvider,
        );
        let wrong = MeshPacket::new(PacketType::Data, "src".to_string());
        assert!(!svc.handle_tip_packet(&wrong));
    }
}
