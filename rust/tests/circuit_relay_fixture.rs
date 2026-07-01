// SPDX-License-Identifier: MIT
//! Cross-language circuit-relay-v2 frame fixture verifier.
//!
//! Reads `../fixtures/circuit-relay/inputs.json` + `../fixtures/circuit-relay/expected/*.bin`
//! and asserts this crate's binary relay frame is byte-identical to the Go oracle
//! (`go/circuitrelay` / C# `AetherNet.CircuitRelay.RelayFrameSerializer`) for every case,
//! then round-trips each back to matching fields. See `fixtures/README.md`.

use std::fs;
use std::path::{Path, PathBuf};

use serde_json::Value;
use uuid::Uuid;

use aethernet_protocol::circuitrelay::{
    deserialize, serialize, MessageType, RelayFrame, Status,
};

fn fixtures_dir() -> PathBuf {
    // CARGO_MANIFEST_DIR = .../rust/, parent = aether-protocol/
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures")
        .join("circuit-relay")
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
    let len = o.get("payload_len").and_then(Value::as_u64).unwrap_or(0) as usize;
    if len > 0 {
        return (0..len).map(|i| (i % 256) as u8).collect();
    }
    hex_decode(o.get("payload_hex").and_then(Value::as_str).unwrap_or(""))
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

fn connection_id(o: &Value) -> Uuid {
    let s = str_field(o, "connection_id");
    if s.is_empty() {
        Uuid::nil()
    } else {
        Uuid::parse_str(s).unwrap_or_else(|e| panic!("invalid connection_id {:?}: {}", s, e))
    }
}

fn message_type(o: &Value) -> MessageType {
    let t = i32_field(o, "type") as u8;
    MessageType::from_u8(t).unwrap_or_else(|| panic!("invalid type {}", t))
}

fn status(o: &Value) -> Status {
    let s = i32_field(o, "status") as u8;
    Status::from_u8(s).unwrap_or_else(|| panic!("invalid status {}", s))
}

fn frame_from(o: &Value) -> RelayFrame {
    RelayFrame {
        message_type: message_type(o),
        status: status(o),
        source_uhid: str_field(o, "source_uhid").to_string(),
        destination_uhid: str_field(o, "destination_uhid").to_string(),
        relay_uhid: str_field(o, "relay_uhid").to_string(),
        connection_id: connection_id(o),
        reservation_expires_at_ms: i64_field(o, "reservation_expires_at_ms"),
        limit_duration_seconds: i32_field(o, "limit_duration_seconds"),
        limit_data_bytes: i64_field(o, "limit_data_bytes"),
        payload: payload_for(o),
    }
}

#[test]
fn relay_serialize_matches_go_oracle() {
    for o in load_inputs() {
        let name = str_field(&o, "name");
        let got = serialize(&frame_from(&o))
            .unwrap_or_else(|e| panic!("{}: serialize failed: {}", name, e));
        assert_eq!(
            got,
            expected(name),
            "fixture {}: serialize bytes diverge — see fixtures/README.md",
            name
        );
    }
}

#[test]
fn relay_deserialize_roundtrips_all_fields() {
    for o in load_inputs() {
        let name = str_field(&o, "name");
        let data = expected(name);
        let f = deserialize(&data)
            .unwrap_or_else(|e| panic!("{}: deserialize failed: {}", name, e));

        assert_eq!(f.message_type, message_type(&o), "{}: type", name);
        assert_eq!(f.status, status(&o), "{}: status", name);
        assert_eq!(f.source_uhid, str_field(&o, "source_uhid"), "{}: source_uhid", name);
        assert_eq!(
            f.destination_uhid,
            str_field(&o, "destination_uhid"),
            "{}: destination_uhid",
            name
        );
        assert_eq!(f.relay_uhid, str_field(&o, "relay_uhid"), "{}: relay_uhid", name);
        assert_eq!(f.connection_id, connection_id(&o), "{}: connection_id", name);
        assert_eq!(
            f.reservation_expires_at_ms,
            i64_field(&o, "reservation_expires_at_ms"),
            "{}: reservation_expires_at_ms",
            name
        );
        assert_eq!(
            f.limit_duration_seconds,
            i32_field(&o, "limit_duration_seconds"),
            "{}: limit_duration_seconds",
            name
        );
        assert_eq!(
            f.limit_data_bytes,
            i64_field(&o, "limit_data_bytes"),
            "{}: limit_data_bytes",
            name
        );
        assert_eq!(f.payload, payload_for(&o), "{}: payload", name);

        // Re-serialize the decoded frame reproduces the oracle bytes exactly.
        let reser = serialize(&f).unwrap_or_else(|e| panic!("{}: re-serialize failed: {}", name, e));
        assert_eq!(reser, data, "{}: re-serialize diverges", name);
    }
}
