// SPDX-License-Identifier: MIT
//! Cross-language BIP-39 recovery-phrase fixture verifier.
//!
//! Reads `../fixtures/bip39/vectors.json` (the official Trezor English vectors)
//! and asserts this crate's BIP-39 codec reproduces all three columns
//! byte-for-byte — the same parity gate the C# reference and the Python port
//! pass. Then exercises the AetherNet identity backup/restore round-trip and the
//! checksum-enforced reject paths. See `fixtures/README.md`.

use std::fs;
use std::path::{Path, PathBuf};

use serde_json::Value;

use aethernet_protocol::security::bip39::{
    entropy_to_mnemonic, mnemonic_to_entropy, mnemonic_to_seed, Bip39Error, IdentityBackup,
};
use aethernet_protocol::security::Ed25519SigningService;

fn fixtures_dir() -> PathBuf {
    // CARGO_MANIFEST_DIR = .../rust/, parent = aether-protocol/
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures")
        .join("bip39")
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

/// All 24 official vectors: entropy -> mnemonic, mnemonic -> entropy, and
/// mnemonic (+ "TREZOR") -> 64-byte seed, each asserted byte-for-byte.
#[test]
fn bip39_official_vectors_match() {
    let root = load_vectors();
    let passphrase = root["passphrase"].as_str().unwrap();
    assert_eq!(passphrase, "TREZOR");

    let vectors = root["vectors"].as_array().unwrap();
    assert_eq!(vectors.len(), 24, "expected 24 official vectors");

    for (i, v) in vectors.iter().enumerate() {
        let entropy_hex = v["entropy"].as_str().unwrap();
        let mnemonic = v["mnemonic"].as_str().unwrap();
        let seed_hex = v["seed"].as_str().unwrap();
        let entropy = unhex(entropy_hex);

        // entropy -> mnemonic
        assert_eq!(
            entropy_to_mnemonic(&entropy).unwrap(),
            mnemonic,
            "vector {i}: entropy_to_mnemonic mismatch"
        );

        // mnemonic -> entropy (checksum enforced)
        assert_eq!(
            hex(&mnemonic_to_entropy(mnemonic).unwrap()),
            entropy_hex,
            "vector {i}: mnemonic_to_entropy mismatch"
        );

        // mnemonic -> 64-byte seed (PBKDF2-HMAC-SHA512, 2048 rounds, "TREZOR")
        assert_eq!(
            hex(&mnemonic_to_seed(mnemonic, passphrase)),
            seed_hex,
            "vector {i}: mnemonic_to_seed mismatch"
        );
    }
}

/// (a) Identity: the specific 32-byte entropy maps to the expected 24-word
/// phrase, and restoring that phrase recovers the exact private seed.
#[test]
fn identity_known_phrase_round_trips() {
    let entropy =
        unhex("f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f");
    let expected_phrase = "void come effort suffer camp survey warrior heavy shoot primary clutch crush open amazing screen patrol group space point ten exist slush involve unfold";

    let phrase = IdentityBackup::to_recovery_phrase(&entropy).unwrap();
    assert_eq!(phrase, expected_phrase, "identity phrase mismatch");

    let (_public_key, private_key) = IdentityBackup::from_recovery_phrase(&phrase).unwrap();
    assert_eq!(private_key, entropy, "restored private key mismatch");
}

/// (b) A fresh 32-byte seed -> phrase -> restore recovers an identical private
/// and public key, and the restored key can sign a message that verifies.
#[test]
fn identity_generated_seed_round_trips_and_signs() {
    let (private_key, public_key) = Ed25519SigningService::generate_keypair();
    assert_eq!(private_key.len(), 32);

    let phrase = IdentityBackup::to_recovery_phrase(&private_key).unwrap();
    let (restored_public, restored_private) =
        IdentityBackup::from_recovery_phrase(&phrase).unwrap();

    assert_eq!(restored_private, private_key, "restored private differs");
    assert_eq!(restored_public, public_key, "restored public differs");

    // The restored key actually works: sign with the restored private, verify
    // with the restored public.
    let message = b"aethernet identity backup restore proof";
    let signature = Ed25519SigningService::sign(&restored_private, message).unwrap();
    assert!(
        Ed25519SigningService::verify(&restored_public, message, &signature),
        "restored key failed to sign+verify"
    );
}

/// (c) Reject paths — every one must return `Err`, never a silently-wrong secret.
#[test]
fn reject_paths_error() {
    // 24 x "abandon" — valid words, valid count, but a bad checksum.
    let all_abandon = vec!["abandon"; 24].join(" ");
    assert_eq!(
        mnemonic_to_entropy(&all_abandon),
        Err(Bip39Error::InvalidChecksum),
        "24x abandon should fail checksum"
    );
    assert!(
        IdentityBackup::from_recovery_phrase(&all_abandon).is_err(),
        "24x abandon should fail restore"
    );

    // An unknown word (not in the wordlist).
    let unknown = "void come effort suffer camp survey warrior heavy shoot primary clutch crush open amazing screen patrol group space point ten exist slush involve notaword";
    assert!(
        matches!(
            mnemonic_to_entropy(unknown),
            Err(Bip39Error::UnknownWord(_))
        ),
        "unknown word should error"
    );
    assert!(IdentityBackup::from_recovery_phrase(unknown).is_err());

    // A 3-word phrase — invalid word count.
    let three = "abandon ability able";
    assert_eq!(
        mnemonic_to_entropy(three),
        Err(Bip39Error::InvalidWordCount(3)),
        "3-word phrase should fail word count"
    );
    assert!(IdentityBackup::from_recovery_phrase(three).is_err());
}
