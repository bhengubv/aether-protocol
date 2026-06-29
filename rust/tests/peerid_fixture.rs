// SPDX-License-Identifier: MIT

//! Cross-language libp2p PeerID parity: the Rust port must reproduce the shared
//! `fixtures/peerid` corpus exactly. The expected values are real `js-libp2p` output, so
//! passing here proves the derivation is both cross-language byte-identical AND
//! interoperable with the real libp2p network.

use aethernet_protocol::identity::peer_id;

fn fixtures_dir() -> std::path::PathBuf {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures/peerid")
}

fn hex_decode(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}

#[test]
fn peerid_byte_parity_with_libp2p_fixture() {
    let dir = fixtures_dir();
    let raw = std::fs::read_to_string(dir.join("inputs.json")).expect("read fixtures/peerid/inputs.json");
    let inputs: serde_json::Value = serde_json::from_str(&raw).expect("parse inputs.json");
    let inputs = inputs.as_array().expect("inputs is an array");
    assert!(!inputs.is_empty(), "no inputs");

    for input in inputs {
        let name = input["name"].as_str().unwrap();
        let pubkey = hex_decode(input["pubkey_hex"].as_str().unwrap());

        let want = std::fs::read_to_string(dir.join("expected").join(format!("{name}.txt")))
            .unwrap_or_else(|e| panic!("{name}: read expected: {e}"));
        let want = want.trim();

        let got = peer_id::from_ed25519_public_key(&pubkey)
            .unwrap_or_else(|e| panic!("{name}: derive: {e}"));

        assert_eq!(got, want, "{name}");
        assert!(
            got.starts_with("12D3Koo"),
            "{name}: expected 12D3Koo prefix, got {got}"
        );
    }
}

#[test]
fn peerid_rejects_wrong_length() {
    assert!(peer_id::from_ed25519_public_key(&[0u8; 31]).is_err());
    assert!(peer_id::from_ed25519_public_key(&[0u8; 33]).is_err());
}
