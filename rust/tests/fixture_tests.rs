// SPDX-License-Identifier: MIT
//! Cross-language wire-format fixture verifier.
//!
//! Reads `../fixtures/inputs.json` and `../fixtures/expected/*.bin` and
//! asserts that this language's `PacketSerializer` produces identical bytes
//! for each canonical input. See `fixtures/README.md`.

use std::fs;
use std::path::{Path, PathBuf};

use serde::Deserialize;
use uuid::Uuid;

use aethermesh_protocol::protocol::{
    serializer::PacketSerializer, MeshPacket, PacketType,
};

#[derive(Debug, Deserialize)]
struct FixtureInput {
    name: String,
    #[allow(dead_code)]
    description: String,
    id: String,
    #[serde(rename = "type")]
    packet_type: u8,
    source_uhid: String,
    destination_uhid: String,
    ttl: i32,
    priority: u8,
    payload_hex: String,
    packet_nonce_hex: String,
    signature_hex: String,
    timestamp_ms: i64,
    protocol_version: u8,
}

fn fixtures_dir() -> PathBuf {
    // CARGO_MANIFEST_DIR = .../rust/, parent = aether-protocol/
    let manifest = Path::new(env!("CARGO_MANIFEST_DIR"));
    manifest.parent().unwrap().join("fixtures")
}

fn hex_decode(s: &str) -> Vec<u8> {
    if s.is_empty() {
        return Vec::new();
    }
    let mut out = Vec::with_capacity(s.len() / 2);
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        let b = u8::from_str_radix(std::str::from_utf8(&bytes[i..i + 2]).unwrap(), 16).unwrap();
        out.push(b);
        i += 2;
    }
    out
}

fn load_inputs() -> Vec<FixtureInput> {
    let path = fixtures_dir().join("inputs.json");
    let raw = fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("read {:?}: {}", path, e));
    serde_json::from_str(&raw).expect("parse inputs.json")
}

fn packet_type_from_byte(b: u8) -> PacketType {
    PacketType::from_byte(b).unwrap_or_else(|| panic!("unknown packet type byte {}", b))
}

fn packet_from(input: &FixtureInput) -> MeshPacket {
    MeshPacket {
        id: Uuid::parse_str(&input.id).unwrap(),
        packet_type: packet_type_from_byte(input.packet_type),
        source_uhid: input.source_uhid.clone(),
        destination_uhid: input.destination_uhid.clone(),
        ttl: input.ttl,
        priority: input.priority,
        payload: hex_decode(&input.payload_hex),
        packet_nonce: hex_decode(&input.packet_nonce_hex),
        signature: hex_decode(&input.signature_hex),
        timestamp_ms: input.timestamp_ms,
        protocol_version: input.protocol_version,
    }
}

#[test]
fn serialize_matches_expected_bytes_for_all_fixtures() {
    for input in load_inputs() {
        let packet = packet_from(&input);
        let got = PacketSerializer::serialize(&packet)
            .unwrap_or_else(|e| panic!("serialize {}: {}", input.name, e));
        let expected_path = fixtures_dir().join("expected").join(format!("{}.bin", input.name));
        let expected = fs::read(&expected_path)
            .unwrap_or_else(|e| panic!("read {:?}: {}", expected_path, e));
        assert_eq!(
            got, expected,
            "fixture {}: bytes diverge — see fixtures/README.md",
            input.name
        );
    }
}

#[test]
fn deserialize_from_expected_matches_input_fields_for_all_fixtures() {
    for input in load_inputs() {
        let expected_path = fixtures_dir().join("expected").join(format!("{}.bin", input.name));
        let bytes = fs::read(&expected_path)
            .unwrap_or_else(|e| panic!("read {:?}: {}", expected_path, e));
        let got = PacketSerializer::deserialize(&bytes)
            .unwrap_or_else(|e| panic!("deserialize {}: {}", input.name, e));

        assert_eq!(got.id, Uuid::parse_str(&input.id).unwrap(), "{}: id", input.name);
        assert_eq!(got.packet_type, packet_type_from_byte(input.packet_type), "{}: type", input.name);
        assert_eq!(got.source_uhid, input.source_uhid, "{}: source", input.name);
        assert_eq!(got.destination_uhid, input.destination_uhid, "{}: dest", input.name);
        assert_eq!(got.ttl, input.ttl, "{}: ttl", input.name);
        assert_eq!(got.priority, input.priority, "{}: priority", input.name);
        assert_eq!(got.payload, hex_decode(&input.payload_hex), "{}: payload", input.name);
        assert_eq!(got.packet_nonce, hex_decode(&input.packet_nonce_hex), "{}: nonce", input.name);
        assert_eq!(got.signature, hex_decode(&input.signature_hex), "{}: signature", input.name);
        assert_eq!(got.timestamp_ms, input.timestamp_ms, "{}: timestamp_ms", input.name);
        assert_eq!(got.protocol_version, input.protocol_version, "{}: protocol_version", input.name);
    }
}
