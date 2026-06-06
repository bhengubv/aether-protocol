// SPDX-License-Identifier: MIT
//
// Cross-language Signal-protocol fixture verifier.
//
// Verifies that the Rust implementation produces byte-identical X3DH and
// ratchet outputs to the C# reference (committed in
// fixtures/signal/expected/*.json). Any drift between Rust and the other
// languages surfaces here as a hex mismatch.

use hkdf::Hkdf;
use hmac::{Hmac, Mac};
use serde_json::Value;
use sha2::Sha256;
use std::fs;
use std::path::PathBuf;
use x25519_dalek::{PublicKey as X25519PublicKey, StaticSecret};

type HmacSha256 = Hmac<Sha256>;

fn repo_root() -> PathBuf {
    let mut p = PathBuf::from(env!("CARGO_MANIFEST_DIR")); // .../aether-protocol/rust
    while !p.join("AetherNetProtocol.slnx").is_file() {
        if !p.pop() {
            panic!("AetherNetProtocol.slnx not found above CARGO_MANIFEST_DIR");
        }
    }
    p
}

fn load_fixture_pair(case_name: &str) -> (Value, Value) {
    let root = repo_root();
    let inputs_path = root.join("fixtures/signal/inputs.json");
    let expected_path = root.join(format!("fixtures/signal/expected/{}.json", case_name));

    let inputs: Value = serde_json::from_str(&fs::read_to_string(inputs_path).unwrap()).unwrap();
    let cases = inputs["cases"].as_array().unwrap();
    let case = cases
        .iter()
        .find(|c| c["name"].as_str() == Some(case_name))
        .unwrap()
        .clone();
    let expected: Value = serde_json::from_str(&fs::read_to_string(expected_path).unwrap()).unwrap();
    (case, expected)
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

fn x25519_derive_pub(priv_bytes: &[u8]) -> [u8; 32] {
    let mut arr = [0u8; 32];
    arr.copy_from_slice(priv_bytes);
    X25519PublicKey::from(&StaticSecret::from(arr)).to_bytes()
}

fn x25519_agree(priv_bytes: &[u8], pub_bytes: &[u8]) -> [u8; 32] {
    let mut p = [0u8; 32];
    p.copy_from_slice(priv_bytes);
    let mut q = [0u8; 32];
    q.copy_from_slice(pub_bytes);
    StaticSecret::from(p)
        .diffie_hellman(&X25519PublicKey::from(q))
        .to_bytes()
}

fn hkdf32(ikm: &[u8], info: &[u8]) -> Vec<u8> {
    let hk = Hkdf::<Sha256>::new(None, ikm);
    let mut out = vec![0u8; 32];
    hk.expand(info, &mut out).unwrap();
    out
}

fn hmac_one(key: &[u8], b: u8) -> Vec<u8> {
    let mut mac = HmacSha256::new_from_slice(key).unwrap();
    mac.update(&[b]);
    mac.finalize().into_bytes().to_vec()
}

#[test]
fn signal_fixture_x3dh_basic() {
    let (inputs, expected) = load_fixture_pair("x3dh_basic");

    let alice_ik = unhex(inputs["alice_identity_priv_hex"].as_str().unwrap());
    let alice_ek = unhex(inputs["alice_ephemeral_priv_hex"].as_str().unwrap());
    let bob_ik = unhex(inputs["bob_identity_priv_hex"].as_str().unwrap());
    let bob_spk = unhex(inputs["bob_signed_pre_key_priv_hex"].as_str().unwrap());
    let bob_opk = unhex(inputs["bob_one_time_pre_key_priv_hex"].as_str().unwrap());

    let alice_ik_pub = x25519_derive_pub(&alice_ik);
    let alice_ek_pub = x25519_derive_pub(&alice_ek);
    let bob_ik_pub = x25519_derive_pub(&bob_ik);
    let bob_spk_pub = x25519_derive_pub(&bob_spk);
    let bob_opk_pub = x25519_derive_pub(&bob_opk);

    let dh1 = x25519_agree(&alice_ik, &bob_spk_pub);
    let dh2 = x25519_agree(&alice_ek, &bob_ik_pub);
    let dh3 = x25519_agree(&alice_ek, &bob_spk_pub);
    let dh4 = x25519_agree(&alice_ek, &bob_opk_pub);

    let mut shared = Vec::new();
    shared.extend_from_slice(&dh1);
    shared.extend_from_slice(&dh2);
    shared.extend_from_slice(&dh3);
    shared.extend_from_slice(&dh4);

    let root_info = inputs["hkdf_root_info_utf8"].as_str().unwrap().as_bytes();
    let send_info = inputs["hkdf_chain_initiator_send_info_utf8"]
        .as_str()
        .unwrap()
        .as_bytes();
    let recv_info = inputs["hkdf_chain_initiator_recv_info_utf8"]
        .as_str()
        .unwrap()
        .as_bytes();

    let root_key = hkdf32(&shared, root_info);
    let send_chain = hkdf32(&root_key, send_info);
    let recv_chain = hkdf32(&root_key, recv_info);

    assert_eq!(hex(&alice_ik_pub), expected["alice_identity_pub_hex"].as_str().unwrap());
    assert_eq!(hex(&alice_ek_pub), expected["alice_ephemeral_pub_hex"].as_str().unwrap());
    assert_eq!(hex(&bob_ik_pub), expected["bob_identity_pub_hex"].as_str().unwrap());
    assert_eq!(hex(&bob_spk_pub), expected["bob_signed_pre_key_pub_hex"].as_str().unwrap());
    assert_eq!(hex(&bob_opk_pub), expected["bob_one_time_pre_key_pub_hex"].as_str().unwrap());
    assert_eq!(hex(&dh1), expected["dh1_hex"].as_str().unwrap());
    assert_eq!(hex(&dh2), expected["dh2_hex"].as_str().unwrap());
    assert_eq!(hex(&dh3), expected["dh3_hex"].as_str().unwrap());
    assert_eq!(hex(&dh4), expected["dh4_hex"].as_str().unwrap());
    assert_eq!(hex(&shared), expected["shared_secret_hex"].as_str().unwrap());
    assert_eq!(hex(&root_key), expected["root_key_hex"].as_str().unwrap());
    assert_eq!(
        hex(&send_chain),
        expected["initiator_send_chain_key_hex"].as_str().unwrap()
    );
    assert_eq!(
        hex(&recv_chain),
        expected["initiator_recv_chain_key_hex"].as_str().unwrap()
    );
}

#[test]
fn signal_fixture_ratchet_step_basic() {
    let (inputs, expected) = load_fixture_pair("ratchet_step_basic");
    let chain_key = unhex(inputs["chain_key_hex"].as_str().unwrap());
    assert_eq!(
        hex(&hmac_one(&chain_key, 0x01)),
        expected["message_key_hex"].as_str().unwrap()
    );
    assert_eq!(
        hex(&hmac_one(&chain_key, 0x02)),
        expected["next_chain_key_hex"].as_str().unwrap()
    );
}

#[test]
fn signal_fixture_ratchet_step_three_iterations() {
    let (inputs, expected) = load_fixture_pair("ratchet_step_three_iterations");
    let mut chain_key = unhex(inputs["initial_chain_key_hex"].as_str().unwrap());
    for i in 0..3 {
        let msg = hmac_one(&chain_key, 0x01);
        let nxt = hmac_one(&chain_key, 0x02);
        assert_eq!(
            hex(&msg),
            expected[format!("step_{}_message_key_hex", i)].as_str().unwrap()
        );
        assert_eq!(
            hex(&nxt),
            expected[format!("step_{}_chain_key_after_hex", i)].as_str().unwrap()
        );
        chain_key = nxt;
    }
}
