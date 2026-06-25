// SPDX-License-Identifier: MIT
//! Cross-language DTN-envelope fixture verifier.
//!
//! Reads `../fixtures/dtn/inputs.json` + `../fixtures/dtn/expected/*.bin` and
//! asserts this crate's binary DTN envelope is byte-identical to the Go oracle
//! (`go/cmd/dtnfixturegen`) for every case, then round-trips each back to
//! matching fields. See `fixtures/README.md`.

use std::fs;
use std::path::{Path, PathBuf};

use serde_json::Value;
use uuid::Uuid;

use aethernet_protocol::dtn::envelope::{
    deserialize_bundle, deserialize_custody_ack, deserialize_delivery_receipt, serialize_bundle,
    serialize_custody_ack, serialize_delivery_receipt,
};
use aethernet_protocol::models::{BundlePriority, BundleStatus, DtnBundle};

fn fixtures_dir() -> PathBuf {
    // CARGO_MANIFEST_DIR = .../rust/, parent = aether-protocol/
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures")
        .join("dtn")
}

fn load_inputs() -> Vec<Value> {
    let path = fixtures_dir().join("inputs.json");
    let raw = fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {:?}: {}", path, e));
    serde_json::from_str(&raw).expect("parse inputs.json")
}

fn expected(name: &str) -> Vec<u8> {
    let p = fixtures_dir().join("expected").join(format!("{}.bin", name));
    fs::read(&p).unwrap_or_else(|e| panic!("read {:?}: {}", p, e))
}

fn hex_decode(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}

fn payload_for(o: &Value) -> Vec<u8> {
    let len = o.get("encrypted_payload_len").and_then(Value::as_u64).unwrap_or(0) as usize;
    if len > 0 {
        return (0..len).map(|i| (i % 256) as u8).collect();
    }
    hex_decode(o.get("encrypted_payload_hex").and_then(Value::as_str).unwrap_or(""))
}

fn str_field<'a>(o: &'a Value, key: &str) -> &'a str {
    o.get(key).and_then(Value::as_str).unwrap_or("")
}

fn i32_field(o: &Value, key: &str) -> i32 {
    o.get(key).and_then(Value::as_i64).unwrap_or(0) as i32
}

fn i64_field(o: &Value, key: &str) -> i64 {
    o.get(key).and_then(Value::as_i64).unwrap_or(0)
}

fn nullable_str(o: &Value, key: &str) -> Option<String> {
    match o.get(key) {
        Some(Value::String(s)) if !s.is_empty() => Some(s.clone()),
        _ => None,
    }
}

fn uuid_field(o: &Value, key: &str) -> Uuid {
    Uuid::parse_str(str_field(o, key)).unwrap()
}

fn bundle_from(o: &Value) -> DtnBundle {
    DtnBundle {
        id: uuid_field(o, "id"),
        sender_uhid: str_field(o, "sender_uhid").to_string(),
        recipient_uhid: str_field(o, "recipient_uhid").to_string(),
        encrypted_payload: payload_for(o),
        priority: BundlePriority::from_u8(i32_field(o, "priority") as u8),
        status: BundleStatus::from_u8(i32_field(o, "status") as u8),
        copy_count: i32_field(o, "copy_count"),
        max_copies: i32_field(o, "max_copies"),
        sender_geohash: nullable_str(o, "sender_geohash"),
        recipient_last_geohash: nullable_str(o, "recipient_last_geohash"),
        hop_count: i32_field(o, "hop_count"),
        created_at: (i64_field(o, "created_at_ms") / 1000) as u64,
        expires_at: (i64_field(o, "expires_at_ms") / 1000) as u64,
    }
}

fn kind(o: &Value) -> &str {
    o.get("kind").and_then(Value::as_str).unwrap()
}

fn serialize_case(o: &Value) -> Vec<u8> {
    match kind(o) {
        "bundle" => serialize_bundle(&bundle_from(o)),
        "custody_ack" => serialize_custody_ack(
            &uuid_field(o, "bundle_id"),
            o.get("accepted").and_then(Value::as_bool).unwrap_or(false),
        ),
        "delivery_receipt" => serialize_delivery_receipt(
            &uuid_field(o, "bundle_id"),
            str_field(o, "recipient_uhid"),
            i32_field(o, "total_hops"),
            i32_field(o, "total_custody_transfers"),
            i64_field(o, "delivered_at_ms"),
        ),
        other => panic!("unknown kind {}", other),
    }
}

#[test]
fn dtn_serialize_matches_go_oracle() {
    for o in load_inputs() {
        let name = str_field(&o, "name");
        assert_eq!(
            serialize_case(&o),
            expected(name),
            "fixture {}: serialize bytes diverge — see fixtures/README.md",
            name
        );
    }
}

#[test]
fn dtn_deserialize_roundtrips_all_fields() {
    for o in load_inputs() {
        let name = str_field(&o, "name");
        let data = expected(name);
        match kind(&o) {
            "bundle" => {
                let b = deserialize_bundle(&data)
                    .unwrap_or_else(|| panic!("{}: deserialize_bundle returned None", name));
                assert_eq!(b.id, uuid_field(&o, "id"), "{}: id", name);
                assert_eq!(b.priority.as_u8(), i32_field(&o, "priority") as u8, "{}: priority", name);
                assert_eq!(b.status.as_u8(), i32_field(&o, "status") as u8, "{}: status", name);
                assert_eq!(b.copy_count, i32_field(&o, "copy_count"), "{}: copy_count", name);
                assert_eq!(b.max_copies, i32_field(&o, "max_copies"), "{}: max_copies", name);
                assert_eq!(b.hop_count, i32_field(&o, "hop_count"), "{}: hop_count", name);
                assert_eq!(
                    b.created_at,
                    (i64_field(&o, "created_at_ms") / 1000) as u64,
                    "{}: created_at",
                    name
                );
                assert_eq!(
                    b.expires_at,
                    (i64_field(&o, "expires_at_ms") / 1000) as u64,
                    "{}: expires_at",
                    name
                );
                assert_eq!(b.sender_uhid, str_field(&o, "sender_uhid"), "{}: sender_uhid", name);
                assert_eq!(b.recipient_uhid, str_field(&o, "recipient_uhid"), "{}: recipient_uhid", name);
                assert_eq!(
                    b.sender_geohash.as_deref().unwrap_or(""),
                    str_field(&o, "sender_geohash"),
                    "{}: sender_geohash",
                    name
                );
                assert_eq!(
                    b.recipient_last_geohash.as_deref().unwrap_or(""),
                    str_field(&o, "recipient_last_geohash"),
                    "{}: recipient_last_geohash",
                    name
                );
                assert_eq!(b.encrypted_payload, payload_for(&o), "{}: payload", name);
            }
            "custody_ack" => {
                let (id, accepted) =
                    deserialize_custody_ack(&data).unwrap_or_else(|| panic!("{}: custody_ack", name));
                assert_eq!(id, uuid_field(&o, "bundle_id"), "{}: bundle_id", name);
                assert_eq!(
                    accepted,
                    o.get("accepted").and_then(Value::as_bool).unwrap_or(false),
                    "{}: accepted",
                    name
                );
            }
            "delivery_receipt" => {
                let (id, recipient, hops, transfers, delivered) =
                    deserialize_delivery_receipt(&data).unwrap_or_else(|| panic!("{}: receipt", name));
                assert_eq!(id, uuid_field(&o, "bundle_id"), "{}: bundle_id", name);
                assert_eq!(recipient, str_field(&o, "recipient_uhid"), "{}: recipient_uhid", name);
                assert_eq!(hops, i32_field(&o, "total_hops"), "{}: total_hops", name);
                assert_eq!(
                    transfers,
                    i32_field(&o, "total_custody_transfers"),
                    "{}: total_custody_transfers",
                    name
                );
                assert_eq!(delivered, i64_field(&o, "delivered_at_ms"), "{}: delivered_at_ms", name);
            }
            other => panic!("unknown kind {}", other),
        }
    }
}
