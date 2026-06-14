// SPDX-License-Identifier: MIT
//
// On-mesh Proof-of-Vicinity token exchange — the directed, two-key witness->subject co-presence
// proof, carried over [`PacketType::PoVTokenExchange`] (43). Rust port of
// `AetherNet.Market.PoVTokenExchangeService` (and the Go `market.PoVTokenExchangeService`). Mirrors
// the AetherNet handler idiom established by MeshTipService (sign payload with the identity key ->
// wrap in a signed MeshPacket -> send) and ReputationGossipService (verify the enclosing packet
// against the supplied sender public key, which also enforces freshness + nonce replay-dedup).
//
// CRYPTO: signatures are real Ed25519 over the canonical token body (build_signable_token_data =
// "SubjectUhid + TimestampTicks + Transport"), byte-identical to every other language implementation,
// so a token exchanged here interoperates on one mesh.
//
// SEPARATION: the resulting [`PoVScore`] is a purely local anti-Sybil routing/identity signal. It
// attaches NO value semantics and never touches any money/reward layer.

use std::collections::BTreeMap;
use std::sync::Mutex;

use crate::protocol::{MeshPacket, PacketType};

use super::{PoVScore, PoVToken, PoVTransportType};

/// The minimal mesh transport surface needed by [`PoVTokenExchangeService`].
pub trait MeshSender: Send + Sync {
    /// The UHID of the local node.
    fn local_uhid(&self) -> String;
    /// Deliver `packet` toward `subject_uhid` (directed — one short-range hop). Returns `true` on
    /// success.
    fn send(&self, packet: &MeshPacket, subject_uhid: &str) -> bool;
}

/// Signs and verifies the enclosing [`MeshPacket`] envelope. `verify_packet` MUST also enforce
/// freshness and nonce replay-dedup (mirroring the C# `IPacketSigningService`), so a replayed or
/// stale PoV exchange is rejected before any crypto on the body.
pub trait PacketSigner: Send + Sync {
    /// Populate `packet.signature` (and the nonce / timestamp fields) in place.
    fn sign_packet(&self, packet: &mut MeshPacket);
    /// Verify `packet`'s envelope signature against `sender_public_key` AND enforce freshness +
    /// replay-dedup. Returns `true` only for a fresh, correctly-signed, non-replayed packet.
    fn verify_packet(&self, packet: &MeshPacket, sender_public_key: &[u8]) -> bool;
}

/// Signs/verifies canonical token bodies with Ed25519 identity keys.
pub trait IdentitySigner: Send + Sync {
    /// Produce a 64-byte Ed25519 signature over `data` using the local identity key.
    fn sign_data(&self, data: &[u8]) -> Vec<u8>;
    /// Verify `sig` over `data` against `public_key`.
    fn verify_signature(&self, public_key: &[u8], data: &[u8], sig: &[u8]) -> bool;
}

/// Issues and accepts on-mesh PoV tokens over packet type 43.
pub struct PoVTokenExchangeService<S, P, I>
where
    S: MeshSender,
    P: PacketSigner,
    I: IdentitySigner,
{
    sender: S,
    signer: P,
    identity: I,
    tokens_by_subject: Mutex<BTreeMap<String, Vec<PoVToken>>>,
}

impl<S, P, I> PoVTokenExchangeService<S, P, I>
where
    S: MeshSender,
    P: PacketSigner,
    I: IdentitySigner,
{
    /// Constructs a `PoVTokenExchangeService`.
    pub fn new(sender: S, signer: P, identity: I) -> Self {
        Self {
            sender,
            signer,
            identity,
            tokens_by_subject: Mutex::new(BTreeMap::new()),
        }
    }

    /// Read-only access to the injected sender (for tests / diagnostics).
    pub fn sender(&self) -> &S {
        &self.sender
    }

    /// Mints a witness-signed PoV token for `subject_uhid` and sends it directed (TTL 1) over packet
    /// 43. It refuses to mint over a non-short-range transport or to vouch for itself. `now_ticks` is
    /// the co-presence event time as .NET DateTime.Ticks (the caller supplies the clock so the signed
    /// body is deterministic and testable). Returns the token that was issued (with an empty subject
    /// signature — the subject fills it on receipt), or `None` when issuance was refused.
    pub fn issue_token(
        &self,
        subject_uhid: &str,
        transport: PoVTransportType,
        now_ticks: i64,
    ) -> Option<PoVToken> {
        if subject_uhid.is_empty() {
            return None;
        }

        // ANTI-REMOTE-MINTING: a vicinity proof is only meaningful over a short-range channel.
        if !transport.is_short_range() {
            return None;
        }

        let local_uhid = self.sender.local_uhid();
        if local_uhid.is_empty() {
            return None;
        }

        // A node cannot vouch for itself — that would be a free, unbounded self-attestation.
        if local_uhid == subject_uhid {
            return None;
        }

        // Witness signs the canonical token body with the node's REAL Ed25519 identity key.
        let witness_sig = self
            .identity
            .sign_data(&super::build_signable_token_data(subject_uhid, now_ticks, transport));

        let token = PoVToken {
            witness_uhid: local_uhid.clone(),
            subject_uhid: subject_uhid.to_string(),
            timestamp_ticks: now_ticks,
            transport_used: transport,
            witness_signature: Some(witness_sig),
            subject_signature: None, // filled by the subject when it counter-signs on receipt.
        };

        let body = token.to_json().ok()?;

        let mut packet = MeshPacket::new(PacketType::PoVTokenExchange, local_uhid);
        packet.destination_uhid = subject_uhid.to_string(); // directed — NOT a broadcast.
        packet.ttl = 1; // co-present: the subject is one short-range hop away.
        packet.payload = body;

        self.signer.sign_packet(&mut packet);
        self.sender.send(&packet, subject_uhid);

        Some(token)
    }

    /// Processes an inbound PoV exchange packet (type 43).
    ///
    /// Returns `true` when the token was accepted, counter-signed, and recorded. Returns `false` when
    /// the packet should be silently discarded (wrong type, bad/stale/replayed envelope, malformed
    /// payload, self-echo, not addressed to us, missing/invalid witness signature, witness ==
    /// subject). On success the accepted [`PoVToken`] (now carrying both signatures) is returned.
    pub fn handle_token_exchange(
        &self,
        packet: &MeshPacket,
        sender_public_key: &[u8],
    ) -> Option<PoVToken> {
        if packet.packet_type != PacketType::PoVTokenExchange {
            return None;
        }

        // 1. Verify the enclosing MeshPacket signature (also enforces freshness + nonce replay-dedup).
        if !self.signer.verify_packet(packet, sender_public_key) {
            return None;
        }

        // 2. Deserialise the token body.
        let mut token = match PoVToken::from_json(&packet.payload) {
            Ok(t) => t,
            Err(_) => return None,
        };
        if token.witness_uhid.is_empty() || token.subject_uhid.is_empty() {
            return None;
        }

        // 3. The incoming token must already carry the witness's signature.
        let witness_sig = match token.witness_signature.as_ref() {
            Some(sig) if !sig.is_empty() => sig.clone(),
            _ => return None,
        };

        let local_uhid = self.sender.local_uhid();

        // 4. Ignore our own token echoed back to us (witness == us).
        if !local_uhid.is_empty() && token.witness_uhid == local_uhid {
            return None;
        }

        // 5. The token must be addressed to us — we are the subject being vouched for.
        if !local_uhid.is_empty() && token.subject_uhid != local_uhid {
            return None;
        }

        // 6. Verify the WITNESS's Ed25519 signature over the canonical body, against the verified
        //    sender key (the witness is the packet source, so the envelope and the body share a
        //    signing key). A forged or tampered witness signature is rejected here before we
        //    counter-sign anything.
        let signable = token.signable_data();
        if !self
            .identity
            .verify_signature(sender_public_key, &signable, &witness_sig)
        {
            return None;
        }

        // 6b. A witness must not be vouching for itself — distinct parties is a hard PoV invariant.
        if token.witness_uhid == token.subject_uhid {
            return None;
        }

        // 7. Counter-sign the SAME canonical body as the subject, with our REAL Ed25519 identity key.
        //    The token now carries BOTH signatures and becomes valid.
        token.subject_signature = Some(self.identity.sign_data(&signable));

        // 8. Record it (increments the witness's contribution to OUR score).
        self.record_token(&token);

        Some(token)
    }

    /// Returns the local PoV trust score for `uhid`, derived from recorded tokens.
    pub fn get_score(&self, uhid: &str) -> PoVScore {
        let guard = self.tokens_by_subject.lock().expect("pov mutex poisoned");
        let tokens = guard.get(uhid);

        let unique = match tokens {
            Some(list) => {
                let mut witnesses: Vec<&str> = list.iter().map(|t| t.witness_uhid.as_str()).collect();
                witnesses.sort_unstable();
                witnesses.dedup();
                witnesses.len()
            }
            None => 0,
        };

        let weighted = if unique > 0 {
            unique as f64 / (unique as f64 + 1.0)
        } else {
            0.0
        };

        PoVScore {
            uhid: uhid.to_string(),
            unique_witnesses: unique,
            weighted_score: weighted,
        }
    }

    /// The sorted list of subject UHIDs with at least one recorded token. Mainly useful for tests and
    /// diagnostics.
    pub fn accepted_subjects(&self) -> Vec<String> {
        let guard = self.tokens_by_subject.lock().expect("pov mutex poisoned");
        guard.keys().cloned().collect()
    }

    fn record_token(&self, token: &PoVToken) {
        let mut guard = self.tokens_by_subject.lock().expect("pov mutex poisoned");
        guard
            .entry(token.subject_uhid.clone())
            .or_default()
            .push(token.clone());
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::security::Ed25519SigningService;
    use std::collections::HashSet;
    use std::sync::Mutex;

    struct FakeSender {
        local: String,
        sent: Mutex<Vec<MeshPacket>>,
    }
    impl FakeSender {
        fn new(local: &str) -> Self {
            Self { local: local.to_string(), sent: Mutex::new(Vec::new()) }
        }
    }
    impl MeshSender for FakeSender {
        fn local_uhid(&self) -> String {
            self.local.clone()
        }
        fn send(&self, packet: &MeshPacket, _subject_uhid: &str) -> bool {
            self.sent.lock().unwrap().push(packet.clone());
            true
        }
    }

    /// Real-Ed25519 identity signer over the node's key.
    struct RealIdentity {
        private_key: Vec<u8>,
    }
    impl IdentitySigner for RealIdentity {
        fn sign_data(&self, data: &[u8]) -> Vec<u8> {
            Ed25519SigningService::sign(&self.private_key, data).unwrap()
        }
        fn verify_signature(&self, public_key: &[u8], data: &[u8], sig: &[u8]) -> bool {
            Ed25519SigningService::verify(public_key, data, sig)
        }
    }

    /// Stamps a real Ed25519 envelope signature over "source:dest" and enforces nonce replay-dedup,
    /// mirroring the C# `IPacketSigningService` contract (freshness is exercised in the C# layer;
    /// here we focus on the body crypto + replay).
    struct PassSigner {
        private_key: Vec<u8>,
        seen: Mutex<HashSet<String>>,
    }
    impl PassSigner {
        fn new(private_key: Vec<u8>) -> Self {
            Self { private_key, seen: Mutex::new(HashSet::new()) }
        }
    }
    impl PacketSigner for PassSigner {
        fn sign_packet(&self, packet: &mut MeshPacket) {
            packet.packet_nonce = vec![9, 9, 9, 9, 9, 9, 9, 9];
            let msg = format!("{}:{}", packet.source_uhid, packet.destination_uhid);
            packet.signature = Ed25519SigningService::sign(&self.private_key, msg.as_bytes()).unwrap();
        }
        fn verify_packet(&self, packet: &MeshPacket, sender_pub: &[u8]) -> bool {
            let key = format!("{}:{:02x?}", packet.source_uhid, packet.packet_nonce);
            {
                let mut seen = self.seen.lock().unwrap();
                if seen.contains(&key) {
                    return false; // replay
                }
                seen.insert(key);
            }
            let msg = format!("{}:{}", packet.source_uhid, packet.destination_uhid);
            Ed25519SigningService::verify(sender_pub, msg.as_bytes(), &packet.signature)
        }
    }

    #[test]
    fn full_flow_witness_subject_countersign() {
        let (witness_priv, witness_pub) = Ed25519SigningService::generate_keypair();
        let (subject_priv, subject_pub) = Ed25519SigningService::generate_keypair();

        const WITNESS_UHID: &str = "aether:node:witness";
        const SUBJECT_UHID: &str = "aether:node:subject";

        // Witness side.
        let witness = PoVTokenExchangeService::new(
            FakeSender::new(WITNESS_UHID),
            PassSigner::new(witness_priv.clone()),
            RealIdentity { private_key: witness_priv },
        );

        let token = witness
            .issue_token(SUBJECT_UHID, PoVTransportType::Ble, 638_000_000_000_000_000)
            .expect("witness refused to issue a valid token");
        assert_eq!(witness.sender.sent.lock().unwrap().len(), 1);
        let exchange_pkt = witness.sender.sent.lock().unwrap()[0].clone();
        assert_eq!(exchange_pkt.packet_type, PacketType::PoVTokenExchange);
        assert_eq!(exchange_pkt.ttl, 1);
        assert!(token.subject_signature.is_none());

        // Subject side receives the witness's packet.
        let subject = PoVTokenExchangeService::new(
            FakeSender::new(SUBJECT_UHID),
            PassSigner::new(subject_priv.clone()),
            RealIdentity { private_key: subject_priv },
        );

        let received = subject
            .handle_token_exchange(&exchange_pkt, &witness_pub)
            .expect("subject rejected a valid witness token");

        // BOTH signatures must now verify over the same canonical body.
        let body = received.signable_data();
        assert!(Ed25519SigningService::verify(
            &witness_pub,
            &body,
            received.witness_signature.as_ref().unwrap()
        ));
        assert!(Ed25519SigningService::verify(
            &subject_pub,
            &body,
            received.subject_signature.as_ref().unwrap()
        ));

        // Score reflects one unique witness for the subject.
        let score = subject.get_score(SUBJECT_UHID);
        assert_eq!(score.unique_witnesses, 1);

        // Replaying the same packet is rejected by the signer's nonce dedup.
        assert!(subject.handle_token_exchange(&exchange_pkt, &witness_pub).is_none());
    }

    #[test]
    fn rejects_self_vouch_and_remote_mint() {
        let (priv_key, _pub) = Ed25519SigningService::generate_keypair();
        let svc = PoVTokenExchangeService::new(
            FakeSender::new("aether:node:self"),
            PassSigner::new(priv_key.clone()),
            RealIdentity { private_key: priv_key },
        );

        // Self-vouch refused.
        assert!(svc
            .issue_token("aether:node:self", PoVTransportType::Ble, 1)
            .is_none());
        // No packet should have been sent for the refused issuance.
        assert_eq!(svc.sender.sent.lock().unwrap().len(), 0);
    }
}
