// SPDX-License-Identifier: MIT

//! Cross-language tipping parity: the Rust port must reproduce the C# reference vectors
//! (fixtures/tipping/tip_packet_basic.json) byte-for-byte — canonical_bytes AND the Ed25519
//! signature. Generated from `TipPacketPayload.BuildCanonicalData + Ed25519`.

use aethernet_protocol::incentive::{
    IdentitySigner, MeshSender, MeshTipService, NoopMeshTipSettlementProvider, PacketSigner,
    RouteResolver, TipPacketPayload,
};
use aethernet_protocol::protocol::{MeshPacket, PacketType};
use aethernet_protocol::security::Ed25519SigningService;
use ed25519_dalek::{Signer, SigningKey};
use uuid::Uuid;

fn load_vectors() -> serde_json::Value {
    let path = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../fixtures/tipping/tip_packet_basic.json"
    );
    let raw = std::fs::read_to_string(path).expect("read fixtures/tipping/tip_packet_basic.json");
    serde_json::from_str(&raw).expect("parse tip_packet_basic.json")
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

/// Rebuilds a `TipPacketPayload` from a fixture case (without the signature).
fn case_to_payload(c: &serde_json::Value) -> TipPacketPayload {
    let reference_id = match c["reference_id"].as_str() {
        Some(s) => Some(Uuid::parse_str(s).expect("valid reference_id")),
        None => None, // JSON null
    };
    TipPacketPayload {
        tipper_uhid: c["tipper_uhid"].as_str().unwrap().to_string(),
        recipient_uhid: c["recipient_uhid"].as_str().unwrap().to_string(),
        amount: c["amount"].as_str().unwrap().to_string(),
        traffic_type: c["traffic_type"].as_str().unwrap().to_string(),
        reference_id,
        timestamp_unix_ms: c["timestamp_unix_ms"].as_i64().unwrap(),
        signature: None,
    }
}

/// Loads the fixture Ed25519 signing key from the seed and asserts the derived public key matches the
/// fixture's published key.
fn signing_key_from_fixture(v: &serde_json::Value) -> SigningKey {
    let seed = from_hex(v["ed25519_seed"].as_str().unwrap());
    let seed_arr: [u8; 32] = seed.as_slice().try_into().expect("seed must be 32 bytes");
    let sk = SigningKey::from_bytes(&seed_arr);
    assert_eq!(
        hex(&sk.verifying_key().to_bytes()),
        v["public_key"].as_str().unwrap(),
        "derived public key must match the fixture's published key"
    );
    sk
}

/// PARITY #1: BuildCanonicalData reproduces the fixture canonical_bytes byte-for-byte for every case
/// (covers null reference_id -> 16 zero bytes, and the .NET mixed-endian GUID byte order).
#[test]
fn tip_canonical_bytes_parity() {
    let v = load_vectors();
    for c in v["cases"].as_array().unwrap() {
        let p = case_to_payload(c);
        assert_eq!(
            hex(&p.build_canonical_data()),
            c["canonical_bytes"].as_str().unwrap(),
            "canonical bytes mismatch for tipper {}",
            c["tipper_uhid"]
        );
    }
}

/// PARITY #2: a fresh Ed25519 sign from the fixture seed reproduces the fixture signature exactly
/// (Ed25519 is deterministic), and the fixture signature verifies against the fixture public key.
#[test]
fn tip_signature_deterministic_parity() {
    let v = load_vectors();
    let sk = signing_key_from_fixture(&v);
    let pub_bytes = sk.verifying_key().to_bytes();

    for c in v["cases"].as_array().unwrap() {
        let p = case_to_payload(c);
        let canonical = p.build_canonical_data();

        // Deterministic re-sign reproduces the exact fixture signature.
        let sig = sk.sign(&canonical);
        assert_eq!(
            hex(&sig.to_bytes()),
            c["signature"].as_str().unwrap(),
            "signature mismatch for tipper {}",
            c["tipper_uhid"]
        );

        // The fixture signature verifies against the fixture public key (via the crate's service).
        let want_sig = from_hex(c["signature"].as_str().unwrap());
        assert!(
            Ed25519SigningService::verify(&pub_bytes, &canonical, &want_sig),
            "fixture signature failed to verify for tipper {}",
            c["tipper_uhid"]
        );
    }
}

/// A signed payload survives a JSON round-trip with canonical bytes and signature intact, and amount
/// stays a string.
#[test]
fn tip_payload_json_round_trip() {
    let v = load_vectors();
    let sk = signing_key_from_fixture(&v);

    for c in v["cases"].as_array().unwrap() {
        let mut p = case_to_payload(c);
        p.signature = Some(sk.sign(&p.build_canonical_data()).to_bytes().to_vec());

        let js = p.to_json().unwrap();
        let back = TipPacketPayload::from_json(&js).unwrap();

        assert_eq!(back.build_canonical_data(), p.build_canonical_data());
        assert_eq!(back.signature, p.signature);
        assert_eq!(back.amount, c["amount"].as_str().unwrap());
        assert_eq!(back.reference_id.is_none(), p.reference_id.is_none());
        assert_eq!(back, p);
    }
}

// ── Service-level flow: the emitted TipPacket(24) carries the exact fixture signature ─────────────

struct NoRoute;
impl RouteResolver for NoRoute {
    fn find_next_hop(&self, _destination_uhid: &str) -> Option<String> {
        None
    }
}

struct FakeSender {
    local: String,
    broadcasts: std::sync::Mutex<Vec<MeshPacket>>,
}
impl MeshSender for FakeSender {
    fn local_uhid(&self) -> String {
        self.local.clone()
    }
    fn send(&self, _packet: &MeshPacket, _next_hop_uhid: &str) -> bool {
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

/// PARITY #3 (service flow): wiring the full MeshTipService send path with the fixture seed yields a
/// signed payload inside the emitted TipPacket(24) carrying the exact fixture signature — proving the
/// service-level flow is byte-identical to C#.
#[test]
fn send_tip_produces_fixture_signature() {
    let v = load_vectors();
    let seed = from_hex(v["ed25519_seed"].as_str().unwrap());

    let c = &v["cases"].as_array().unwrap()[0];
    let sender = FakeSender {
        local: c["tipper_uhid"].as_str().unwrap().to_string(),
        broadcasts: std::sync::Mutex::new(Vec::new()),
    };
    let svc = MeshTipService::new(
        sender,
        FakeSigner,
        SeedIdentity { private_key: seed },
        None::<NoRoute>,
        NoopMeshTipSettlementProvider,
    );

    let reference_id = Uuid::parse_str(c["reference_id"].as_str().unwrap()).unwrap();
    let signed = svc
        .send_tip(
            c["recipient_uhid"].as_str().unwrap(),
            c["amount"].as_str().unwrap(),
            c["traffic_type"].as_str().unwrap(),
            Some(reference_id),
            c["timestamp_unix_ms"].as_i64().unwrap(),
        )
        .unwrap();
    assert_eq!(signed.packet_type, PacketType::TipPacket);

    let payload = TipPacketPayload::from_json(&signed.payload).unwrap();
    assert_eq!(
        hex(payload.signature.as_ref().unwrap()),
        c["signature"].as_str().unwrap(),
        "service-emitted signature must match the fixture signature"
    );
}
