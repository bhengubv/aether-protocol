// SPDX-License-Identifier: MIT
//! Cross-language BLE tracking-protection fixture verifier.
//!
//! Reads `../fixtures/bleprivacy/vectors.json` and asserts this crate's
//! `security::ble_privacy` reproduces the rotating Service UUID and IRK-based
//! Resolvable Private Address (RPA) byte-for-byte — the same parity gate the C#
//! reference `BlePrivacy` passes. The time window is a little-endian `i64`.
//! See `fixtures/README.md`.

use std::fs;
use std::path::{Path, PathBuf};

use serde_json::Value;

use aethernet_protocol::security::ble_privacy::{
    resolvable_address, resolve_address, service_uuid, window_for, ROTATION_SECONDS,
};

fn fixtures_dir() -> PathBuf {
    // CARGO_MANIFEST_DIR = .../rust/, parent = aether-protocol/
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures")
        .join("bleprivacy")
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

/// The full parity gate: every UUID vector, every RPA vector (derive + resolve +
/// wrong-IRK reject), the rotation constant, the window boundary, and the
/// 15-byte-IRK reject path.
#[test]
fn ble_privacy_vectors_match() {
    let root = load_vectors();

    let rotation_key = unhex(root["rotation_key"].as_str().unwrap());
    let irk = unhex(root["irk"].as_str().unwrap());
    let wrong_irk = unhex(root["wrong_irk"].as_str().unwrap());
    assert_eq!(irk.len(), 16, "fixture IRK must be 16 bytes");
    assert_eq!(wrong_irk.len(), 16, "fixture wrong_irk must be 16 bytes");

    // ROTATION_SECONDS == rotation_seconds (900).
    let rotation_seconds = root["rotation_seconds"].as_i64().unwrap();
    assert_eq!(
        ROTATION_SECONDS, rotation_seconds,
        "ROTATION_SECONDS mismatch"
    );

    // uuid_vectors: service_uuid(rotation_key, window) == uuid.
    let uuid_vectors = root["uuid_vectors"].as_array().unwrap();
    assert!(!uuid_vectors.is_empty(), "expected uuid vectors");
    for (i, v) in uuid_vectors.iter().enumerate() {
        let window = v["window"].as_i64().unwrap();
        let expected = v["uuid"].as_str().unwrap();
        assert_eq!(
            service_uuid(&rotation_key, window),
            expected,
            "uuid vector {i} (window {window}) mismatch"
        );
    }

    // rpa_vectors: hex(resolvable_address(irk, window)) == rpa, resolves with the
    // right IRK, and is rejected by the wrong IRK.
    let rpa_vectors = root["rpa_vectors"].as_array().unwrap();
    assert!(!rpa_vectors.is_empty(), "expected rpa vectors");
    for (i, v) in rpa_vectors.iter().enumerate() {
        let window = v["window"].as_i64().unwrap();
        let expected = v["rpa"].as_str().unwrap();

        let rpa = resolvable_address(&irk, window)
            .unwrap_or_else(|| panic!("rpa vector {i}: 16-byte IRK should produce an RPA"));
        assert_eq!(
            hex(&rpa),
            expected,
            "rpa vector {i} (window {window}) mismatch"
        );

        // Round-trip: the generating IRK resolves the RPA it produced.
        assert!(
            resolve_address(&irk, &rpa),
            "rpa vector {i}: correct IRK failed to resolve its own RPA"
        );
        // And a different IRK must not.
        assert!(
            !resolve_address(&wrong_irk, &rpa),
            "rpa vector {i}: wrong IRK wrongly resolved the RPA"
        );

        // Also resolve the fixture's own hex bytes directly (not just our output).
        let rpa_bytes = unhex(expected);
        assert!(
            resolve_address(&irk, &rpa_bytes),
            "rpa vector {i}: correct IRK failed to resolve fixture RPA bytes"
        );
        assert!(
            !resolve_address(&wrong_irk, &rpa_bytes),
            "rpa vector {i}: wrong IRK wrongly resolved fixture RPA bytes"
        );
    }

    // window_for boundary: 899 -> 0, 900 -> 1.
    assert_eq!(window_for(899), 0, "window_for(899) should be 0");
    assert_eq!(window_for(900), 1, "window_for(900) should be 1");

    // A 15-byte IRK is rejected: resolvable_address -> None, resolve_address -> false.
    let short_irk = &irk[..15];
    assert!(
        resolvable_address(short_irk, 0).is_none(),
        "15-byte IRK should be rejected by resolvable_address"
    );
    // resolve_address with a valid-length RPA but a short IRK is also false.
    let sample_rpa = resolvable_address(&irk, 0).unwrap();
    assert!(
        !resolve_address(short_irk, &sample_rpa),
        "15-byte IRK should be rejected by resolve_address"
    );
}
