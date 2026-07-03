// SPDX-License-Identifier: MIT
//! Cross-language multi-device-sync fixture verifier.
//!
//! Reads `../fixtures/sync/vectors.json` and asserts this crate's sync codec is
//! byte-identical to the C# reference (and every other AetherNet SDK) for:
//!
//!  * `SyncRecord` — `serialize` hex matches `serialized_hex`, and `deserialize`
//!    round-trips every field.
//!  * Reconcile (LWW) — `winner(records).record_id == winner_record_id`, and the
//!    same winner regardless of input order (also tested reversed).
//!  * `DeviceLink` — `signed_body` hex, the deterministic Ed25519 `signature`
//!    hex, the full `serialize` hex, `verify` true under the identity public key
//!    and false under the wrong one, and a `deserialize` round-trip.
//!
//! See `fixtures/README.md`.

use std::fs;
use std::path::{Path, PathBuf};

use serde_json::Value;
use uuid::Uuid;

use aethernet_protocol::sync::device_link::{
    self, deserialize as dl_deserialize, serialize as dl_serialize, signed_body, verify, DeviceLink,
};
use aethernet_protocol::sync::reconciler::winner;
use aethernet_protocol::sync::record::{
    deserialize as rec_deserialize, serialize as rec_serialize, SyncOp, SyncRecord,
};

fn fixtures_dir() -> PathBuf {
    // CARGO_MANIFEST_DIR = .../rust/, parent = aether-protocol/
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures")
        .join("sync")
}

fn load_vectors() -> Value {
    let path = fixtures_dir().join("vectors.json");
    let raw = fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {:?}: {}", path, e));
    serde_json::from_str(&raw).expect("parse vectors.json")
}

fn unhex(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}

fn hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{:02x}", x)).collect()
}

fn unhex32(s: &str) -> [u8; 32] {
    unhex(s).as_slice().try_into().expect("expected 32 bytes")
}

/// The 16-byte RFC-4122 big-endian bytes of a UUID string — exactly what the C#
/// `Guid(bigEndian: true)` and the Rust `Uuid::as_bytes` produce.
fn record_id_bytes(s: &str) -> [u8; 16] {
    *Uuid::parse_str(s).unwrap().as_bytes()
}

fn op_from_u8(v: u64) -> SyncOp {
    SyncOp::from_u8(v as u8).unwrap()
}

fn record_from(o: &Value) -> SyncRecord {
    SyncRecord::new(
        record_id_bytes(o["record_id"].as_str().unwrap()),
        o["device_id"].as_str().unwrap().to_string(),
        op_from_u8(o["op"].as_u64().unwrap()),
        o["item_id"].as_str().unwrap().to_string(),
        o["logical_clock"].as_i64().unwrap(),
        o["created_at_ms"].as_i64().unwrap(),
        unhex(o["payload_hex"].as_str().unwrap()),
    )
}

// ─────────────────────────────── SyncRecord ───────────────────────────────

#[test]
fn sync_records_serialize_and_roundtrip() {
    let root = load_vectors();
    let records = root["sync_records"].as_array().unwrap();
    assert!(!records.is_empty(), "expected sync_records");

    for (i, o) in records.iter().enumerate() {
        let rec = record_from(o);
        let expected_hex = o["serialized_hex"].as_str().unwrap();

        // serialize -> hex matches the fixture byte-for-byte.
        let bytes = rec_serialize(&rec).expect("serialize");
        assert_eq!(hex(&bytes), expected_hex, "record {i}: serialize hex mismatch");

        // deserialize round-trips every field.
        let back = rec_deserialize(&unhex(expected_hex)).expect("deserialize");
        assert_eq!(back, rec, "record {i}: deserialize round-trip mismatch");
        assert_eq!(
            back.record_id,
            record_id_bytes(o["record_id"].as_str().unwrap()),
            "record {i}: record_id"
        );
    }
}

// ─────────────────────────── Reconcile (LWW) ──────────────────────────────

#[test]
fn reconcile_winner_matches_and_is_order_independent() {
    let root = load_vectors();
    let cases = root["reconcile"].as_array().unwrap();
    assert!(!cases.is_empty(), "expected reconcile cases");

    for case in cases {
        let name = case["name"].as_str().unwrap();
        let mut records: Vec<SyncRecord> = case["records"]
            .as_array()
            .unwrap()
            .iter()
            .map(record_from)
            .collect();
        let expected = record_id_bytes(case["winner_record_id"].as_str().unwrap());

        let w = winner(&records).unwrap_or_else(|| panic!("{name}: no winner"));
        assert_eq!(w.record_id, expected, "{name}: winner mismatch");

        // Same winner with the input reversed — order independence.
        records.reverse();
        let w_rev = winner(&records).unwrap_or_else(|| panic!("{name}: no winner (reversed)"));
        assert_eq!(
            w_rev.record_id, expected,
            "{name}: winner mismatch on reversed input"
        );
    }
}

// ─────────────────────────────── DeviceLink ───────────────────────────────

#[test]
fn device_links_sign_serialize_and_verify() {
    let root = load_vectors();
    let identity_private = unhex(root["identity_private"].as_str().unwrap());
    let identity_public = unhex(root["identity_public"].as_str().unwrap());
    let wrong_identity_public = unhex(root["wrong_identity_public"].as_str().unwrap());

    let links = root["device_links"].as_array().unwrap();
    assert!(!links.is_empty(), "expected device_links");

    for (i, o) in links.iter().enumerate() {
        let device_id = o["device_id"].as_str().unwrap();
        let device_public_key = unhex32(o["device_public_key"].as_str().unwrap());
        let issued_at_ms = o["issued_at_ms"].as_i64().unwrap();

        // signed_body hex matches.
        let body = signed_body(device_id, &device_public_key, issued_at_ms).expect("signed_body");
        assert_eq!(
            hex(&body),
            o["signed_body_hex"].as_str().unwrap(),
            "link {i}: signed_body hex mismatch"
        );

        // create -> deterministic Ed25519 signature hex matches.
        let link =
            device_link::create(device_id, device_public_key, issued_at_ms, &identity_private)
                .expect("create");
        assert_eq!(
            hex(&link.signature),
            o["signature_hex"].as_str().unwrap(),
            "link {i}: signature hex mismatch"
        );

        // serialize hex (body ++ signature) matches.
        let serialized = dl_serialize(&link).expect("serialize");
        assert_eq!(
            hex(&serialized),
            o["serialized_hex"].as_str().unwrap(),
            "link {i}: serialize hex mismatch"
        );

        // verify: true under the real identity public key, false under the wrong one.
        assert!(
            verify(&link, &identity_public),
            "link {i}: verify under identity_public should be true"
        );
        assert!(
            !verify(&link, &wrong_identity_public),
            "link {i}: verify under wrong_identity_public should be false"
        );

        // deserialize round-trips.
        let back: DeviceLink = dl_deserialize(&serialized).expect("deserialize");
        assert_eq!(back, link, "link {i}: deserialize round-trip mismatch");
    }
}
