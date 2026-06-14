// SPDX-License-Identifier: MIT

//! Cross-language Proof-of-Vicinity parity: the Rust port must reproduce the C# reference vectors
//! (fixtures/market/pov_token_basic.json) byte-for-byte — canonical_body AND the witness Ed25519
//! signature. Generated from `PoVTokenCodec.BuildSignableTokenData + Ed25519`.

use aethernet_protocol::market::{
    build_signable_token_data, IdentitySigner, MeshSender, PacketSigner, PoVToken,
    PoVTokenExchangeService, PoVTransportType,
};
use aethernet_protocol::protocol::{MeshPacket, PacketType};
use aethernet_protocol::security::Ed25519SigningService;
use ed25519_dalek::{Signer, SigningKey};

fn load_vectors() -> serde_json::Value {
    let path = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../fixtures/market/pov_token_basic.json"
    );
    let raw = std::fs::read_to_string(path).expect("read fixtures/market/pov_token_basic.json");
    serde_json::from_str(&raw).expect("parse pov_token_basic.json")
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn from_hex(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("valid hex"))
        .collect()
}

fn signing_key_from_fixture(v: &serde_json::Value) -> SigningKey {
    let seed = from_hex(v["witness_seed"].as_str().unwrap());
    let seed_arr: [u8; 32] = seed.as_slice().try_into().expect("seed must be 32 bytes");
    let sk = SigningKey::from_bytes(&seed_arr);
    assert_eq!(
        hex(&sk.verifying_key().to_bytes()),
        v["witness_public_key"].as_str().unwrap(),
        "derived witness public key must match the fixture's published key"
    );
    sk
}

/// PARITY #1: BuildSignableTokenData reproduces the fixture canonical_body byte-for-byte for every
/// case (covers all three transports + the .NET DateTime.Ticks i64 LE field).
#[test]
fn pov_canonical_body_parity() {
    let v = load_vectors();
    for c in v["cases"].as_array().unwrap() {
        let transport_byte = c["transport_byte"].as_u64().unwrap() as u8;
        let transport = PoVTransportType::from_byte(transport_byte).expect("known transport");
        let body = build_signable_token_data(
            c["subject_uhid"].as_str().unwrap(),
            c["timestamp_ticks"].as_i64().unwrap(),
            transport,
        );
        assert_eq!(
            hex(&body),
            c["canonical_body"].as_str().unwrap(),
            "canonical body mismatch for subject {}",
            c["subject_uhid"]
        );
        // Transport enum byte must match the named transport.
        assert_eq!(
            transport.as_str(),
            c["transport"].as_str().unwrap(),
            "transport name mismatch"
        );
    }
}

/// PARITY #2: a fresh Ed25519 sign from the fixture witness seed reproduces the fixture
/// witness_signature exactly (Ed25519 is deterministic), and it verifies against the witness public
/// key.
#[test]
fn pov_witness_signature_deterministic_parity() {
    let v = load_vectors();
    let sk = signing_key_from_fixture(&v);
    let pub_bytes = sk.verifying_key().to_bytes();

    for c in v["cases"].as_array().unwrap() {
        let transport =
            PoVTransportType::from_byte(c["transport_byte"].as_u64().unwrap() as u8).unwrap();
        let body = build_signable_token_data(
            c["subject_uhid"].as_str().unwrap(),
            c["timestamp_ticks"].as_i64().unwrap(),
            transport,
        );

        let sig = sk.sign(&body);
        assert_eq!(
            hex(&sig.to_bytes()),
            c["witness_signature"].as_str().unwrap(),
            "witness signature mismatch for subject {}",
            c["subject_uhid"]
        );

        let want_sig = from_hex(c["witness_signature"].as_str().unwrap());
        assert!(
            Ed25519SigningService::verify(&pub_bytes, &body, &want_sig),
            "fixture witness signature failed to verify for subject {}",
            c["subject_uhid"]
        );
    }
}

/// A token with the witness signature survives a JSON round-trip with its canonical body intact, and
/// the transport stays a numeric wire byte.
#[test]
fn pov_token_json_round_trip() {
    let v = load_vectors();
    let sk = signing_key_from_fixture(&v);

    for c in v["cases"].as_array().unwrap() {
        let transport =
            PoVTransportType::from_byte(c["transport_byte"].as_u64().unwrap() as u8).unwrap();
        let body = build_signable_token_data(
            c["subject_uhid"].as_str().unwrap(),
            c["timestamp_ticks"].as_i64().unwrap(),
            transport,
        );
        let tok = PoVToken {
            witness_uhid: "aether:witness:zz".to_string(),
            subject_uhid: c["subject_uhid"].as_str().unwrap().to_string(),
            timestamp_ticks: c["timestamp_ticks"].as_i64().unwrap(),
            transport_used: transport,
            witness_signature: Some(sk.sign(&body).to_bytes().to_vec()),
            subject_signature: None,
        };

        let js = tok.to_json().unwrap();
        let back = PoVToken::from_json(&js).unwrap();
        assert_eq!(back.signable_data(), tok.signable_data());
        assert_eq!(back.witness_signature, tok.witness_signature);
        assert_eq!(back.transport_used, tok.transport_used);
        assert_eq!(back, tok);
    }
}

// ── Service-level flow: witness issues over packet 43, subject verifies + countersigns ────────────

struct FakeSender {
    local: String,
    sent: std::sync::Mutex<Vec<MeshPacket>>,
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

/// A pass-through signer that stamps a real Ed25519 envelope signature over "source:dest" and
/// enforces nonce replay-dedup (mirroring the C# `IPacketSigningService` contract).
struct PassSigner {
    private_key: Vec<u8>,
    seen: std::sync::Mutex<std::collections::HashSet<String>>,
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

/// PARITY #3 (service flow): the witness signs the fixture canonical body with the fixture seed and
/// emits a PoVTokenExchange(43) packet whose embedded witness signature equals the fixture signature;
/// the subject then verifies that witness signature, counter-signs, and BOTH signatures verify.
#[test]
fn pov_exchange_emits_fixture_witness_signature_and_countersigns() {
    let v = load_vectors();
    let witness_seed = from_hex(v["witness_seed"].as_str().unwrap());
    let witness_pub = from_hex(v["witness_public_key"].as_str().unwrap());
    let c = &v["cases"].as_array().unwrap()[0];

    let subject_uhid = c["subject_uhid"].as_str().unwrap();
    let timestamp_ticks = c["timestamp_ticks"].as_i64().unwrap();
    let transport =
        PoVTransportType::from_byte(c["transport_byte"].as_u64().unwrap() as u8).unwrap();

    // Witness side — local UHID must differ from the subject (no self-vouch).
    let witness = PoVTokenExchangeService::new(
        FakeSender {
            local: "aether:witness:node".to_string(),
            sent: std::sync::Mutex::new(Vec::new()),
        },
        PassSigner {
            private_key: witness_seed.clone(),
            seen: std::sync::Mutex::new(std::collections::HashSet::new()),
        },
        RealIdentity { private_key: witness_seed },
    );

    let token = witness
        .issue_token(subject_uhid, transport, timestamp_ticks)
        .expect("witness should issue a valid token");

    // The witness signature embedded in the issued token equals the fixture signature exactly —
    // proving the service-level signing flow is byte-identical to C#.
    assert_eq!(
        hex(token.witness_signature.as_ref().unwrap()),
        c["witness_signature"].as_str().unwrap(),
        "service-emitted witness signature must match the fixture signature"
    );

    let exchange_pkt = {
        let sent = witness.sender().sent.lock().unwrap();
        assert_eq!(sent.len(), 1);
        assert_eq!(sent[0].packet_type, PacketType::PoVTokenExchange);
        assert_eq!(sent[0].ttl, 1);
        sent[0].clone()
    };

    // Subject side verifies the witness signature, counter-signs, records.
    let (subject_priv, subject_pub) = Ed25519SigningService::generate_keypair();
    let subject = PoVTokenExchangeService::new(
        FakeSender {
            local: subject_uhid.to_string(),
            sent: std::sync::Mutex::new(Vec::new()),
        },
        PassSigner {
            private_key: subject_priv.clone(),
            seen: std::sync::Mutex::new(std::collections::HashSet::new()),
        },
        RealIdentity { private_key: subject_priv },
    );

    let accepted = subject
        .handle_token_exchange(&exchange_pkt, &witness_pub)
        .expect("subject should accept the valid witness token");

    // BOTH signatures verify over the same canonical body.
    let body = accepted.signable_data();
    assert!(Ed25519SigningService::verify(
        &witness_pub,
        &body,
        accepted.witness_signature.as_ref().unwrap()
    ));
    assert!(Ed25519SigningService::verify(
        &subject_pub,
        &body,
        accepted.subject_signature.as_ref().unwrap()
    ));

    // Score reflects one unique witness; replay is rejected.
    assert_eq!(subject.get_score(subject_uhid).unique_witnesses, 1);
    assert!(subject.handle_token_exchange(&exchange_pkt, &witness_pub).is_none());
}
