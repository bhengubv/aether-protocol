// SPDX-License-Identifier: MIT
//! Cross-language panic-wipe parity verifier.
//!
//! Reads `../fixtures/panicwipe/vectors.json` and asserts this crate's
//! panic-wipe core reproduces the shared vectors byte-for-byte — the same parity
//! gate the C# reference passes:
//!
//! - each duress PIN hashes to the recorded SHA-256, verifies against it, and a
//!   tampered PIN does not,
//! - the identity key-name manifest, pre-key name patterns and `MAX_PRE_KEYS`
//!   match the fixture exactly,
//! - `secure_erase` zeroes a buffer, and `verify_duress_pin` rejects a
//!   wrong-length stored hash.
//!
//! See `fixtures/README.md`.

use std::fs;
use std::path::{Path, PathBuf};

use serde_json::Value;

use aethernet_protocol::security::panic_wipe::{
    duress_pin_hash, pre_key_name, secure_erase, signed_pre_key_name, verify_duress_pin,
    IDENTITY_KEY_NAMES, MAX_PRE_KEYS,
};

fn fixtures_dir() -> PathBuf {
    // CARGO_MANIFEST_DIR = .../rust/, parent = aether-protocol/
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures")
        .join("panicwipe")
}

fn load_vectors() -> Value {
    let path = fixtures_dir().join("vectors.json");
    let raw = fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {:?}: {}", path, e));
    serde_json::from_str(&raw).expect("parse vectors.json")
}

fn hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{:02x}", x)).collect()
}

/// Every duress-PIN vector: PIN -> recorded SHA-256, verify true against it, and
/// a one-character-mutated PIN verifies false.
#[test]
fn duress_pin_vectors_match() {
    let root = load_vectors();
    let vectors = root["duress_pin_hashes"].as_array().unwrap();
    assert!(!vectors.is_empty(), "expected duress_pin vectors");

    for (i, v) in vectors.iter().enumerate() {
        let pin = v["pin"].as_str().unwrap();
        let expected = v["sha256"].as_str().unwrap();

        let hash = duress_pin_hash(pin);
        assert_eq!(hex(&hash), expected, "vector {i}: duress_pin_hash mismatch");

        // The genuine PIN verifies in constant time against its stored hash.
        assert!(
            verify_duress_pin(pin, &hash),
            "vector {i}: verify_duress_pin should accept the genuine PIN"
        );

        // A tampered PIN (one extra char) must not verify against the same hash.
        let tampered = pin.to_string() + "x";
        assert!(
            !verify_duress_pin(&tampered, &hash),
            "vector {i}: verify_duress_pin should reject a mutated PIN"
        );
    }
}

/// The wipe manifest — identity key names, pre-key name patterns and the pre-key
/// slot count — matches the shared fixture exactly.
#[test]
fn wipe_manifest_matches() {
    let root = load_vectors();

    // MAX_PRE_KEYS.
    let max_prekeys = root["max_prekeys"].as_u64().unwrap() as usize;
    assert_eq!(MAX_PRE_KEYS, max_prekeys, "max_prekeys mismatch");

    // IDENTITY_KEY_NAMES, in order.
    let expected_names: Vec<&str> = root["identity_key_names"]
        .as_array()
        .unwrap()
        .iter()
        .map(|n| n.as_str().unwrap())
        .collect();
    assert_eq!(
        IDENTITY_KEY_NAMES, expected_names,
        "identity_key_names mismatch"
    );

    // pre_key_name(index) == expected.
    let pk = &root["prekey_name"];
    let pk_index = pk["index"].as_u64().unwrap() as usize;
    let pk_expected = pk["expected"].as_str().unwrap();
    assert_eq!(pre_key_name(pk_index), pk_expected, "prekey_name mismatch");

    // signed_pre_key_name(index) == expected.
    let spk = &root["signed_prekey_name"];
    let spk_index = spk["index"].as_u64().unwrap() as usize;
    let spk_expected = spk["expected"].as_str().unwrap();
    assert_eq!(
        signed_pre_key_name(spk_index),
        spk_expected,
        "signed_prekey_name mismatch"
    );
}

/// Behavioural checks that are not deterministic across languages but must hold:
/// secure_erase zeroes its buffer, and a wrong-length stored hash is rejected.
#[test]
fn secure_erase_and_reject_paths() {
    // secure_erase leaves an all-zero buffer.
    let mut buf = vec![0xAAu8; 48];
    secure_erase(&mut buf);
    assert!(buf.iter().all(|&b| b == 0), "secure_erase left non-zero bytes");

    // verify_duress_pin with a 16-byte stored hash -> false (length gate).
    assert!(
        !verify_duress_pin("1234", &[0u8; 16]),
        "verify_duress_pin should reject a 16-byte hash"
    );
}
