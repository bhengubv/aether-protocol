// SPDX-License-Identifier: MIT
//! Cross-language P-256 ECDSA verify fixture runner (Rust).
//!
//! Drives `Ed25519SigningService::verify_with_fallback` through the shared corpus at
//! `tests/cross-language/p256-fixtures.json` — DER SubjectPublicKeyInfo public key +
//! ASN.1 DER ECDSA signature + SHA-256, per PROTOCOL_SPEC.md §7.5. Every AetherNet SDK
//! drives the SAME vectors and MUST accept valid:true and reject valid:false.

use aethernet_protocol::security::Ed25519SigningService;

const FIXTURE_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../tests/cross-language/p256-fixtures.json"
));

fn hex_decode(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}

#[test]
fn verify_with_fallback_drives_every_p256_vector() {
    let doc: serde_json::Value = serde_json::from_str(FIXTURE_JSON).expect("valid JSON");
    let vectors = doc["vectors"].as_array().expect("vectors array");
    assert!(!vectors.is_empty(), "no vectors");

    for v in vectors {
        let name = v["name"].as_str().unwrap();
        let pub_key = hex_decode(v["public_key_der"].as_str().unwrap());
        let msg = hex_decode(v["message"].as_str().unwrap());
        let sig = hex_decode(v["signature_der"].as_str().unwrap());
        let expected = v["valid"].as_bool().unwrap();

        // A >32-byte key forces the P-256 branch; an Ed25519-only regression would
        // reject the valid vector and fail here.
        assert!(pub_key.len() > 32, "{name}: P-256 key must be > 32 bytes");
        assert_eq!(
            Ed25519SigningService::verify_with_fallback(&pub_key, &msg, &sig),
            expected,
            "{name}"
        );
    }
}
