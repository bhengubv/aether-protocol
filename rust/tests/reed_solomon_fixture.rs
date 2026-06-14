// SPDX-License-Identifier: MIT

//! Cross-language vault parity: the Rust systematic Cauchy-Reed-Solomon codec must reproduce the C#
//! reference vectors (fixtures/vault/reed_solomon_basic.json) byte-for-byte — every shard and every
//! recovery byte. K=10, M=4, GF(2^8) primitive polynomial 0x11D, alpha=2.

use aethernet_protocol::vault::{ReedSolomonCodec, VaultError};
use std::collections::BTreeMap;

fn load_vectors() -> serde_json::Value {
    let path = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../fixtures/vault/reed_solomon_basic.json"
    );
    let raw = std::fs::read_to_string(path).expect("read fixtures/vault/reed_solomon_basic.json");
    serde_json::from_str(&raw).expect("parse reed_solomon_basic.json")
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn from_hex(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("valid hex"))
        .collect()
}

fn params(v: &serde_json::Value) -> (usize, usize, usize, usize) {
    (
        v["k"].as_u64().unwrap() as usize,
        v["m"].as_u64().unwrap() as usize,
        v["n"].as_u64().unwrap() as usize,
        v["input_size"].as_u64().unwrap() as usize,
    )
}

/// PARITY #1: the Rust encoder reproduces every C# shard (systematic data + Cauchy parity)
/// byte-for-byte.
#[test]
fn reed_solomon_shard_parity() {
    let v = load_vectors();
    let (k, m, n, input_size) = params(&v);
    assert_eq!((k, m, n), (10, 4, 14), "unexpected fixture params");

    let input = from_hex(v["input"].as_str().unwrap());
    assert_eq!(input.len(), input_size);

    let codec = ReedSolomonCodec::new(k, m).unwrap();
    let shards = codec.encode_data(&input).unwrap();
    assert_eq!(shards.len(), n);
    assert_eq!(
        shards[0].len(),
        v["shard_size"].as_u64().unwrap() as usize,
        "shard size"
    );

    for want in v["shards"].as_array().unwrap() {
        let idx = want["index"].as_u64().unwrap() as usize;
        assert_eq!(
            hex(&shards[idx]),
            want["hex"].as_str().unwrap(),
            "shard {idx} mismatch"
        );
    }
}

/// PARITY #2: every recovery subset decodes to the fixture input byte-for-byte (covers the systematic
/// fast-path, the all-parity path, and a data+parity mix).
#[test]
fn reed_solomon_recovery_parity() {
    let v = load_vectors();
    let (k, m, _n, input_size) = params(&v);
    let input = from_hex(v["input"].as_str().unwrap());

    let codec = ReedSolomonCodec::new(k, m).unwrap();
    let shards = codec.encode_data(&input).unwrap();

    for rec in v["recovery"].as_array().unwrap() {
        let mut available: BTreeMap<usize, Vec<u8>> = BTreeMap::new();
        for idx in rec["survivor_indices"].as_array().unwrap() {
            let i = idx.as_u64().unwrap() as usize;
            available.insert(i, shards[i].clone());
        }

        let recovered = codec
            .reconstruct_data(&available, input_size)
            .unwrap_or_else(|e| panic!("recovery {:?} failed: {e}", rec["note"]));

        assert_eq!(
            hex(&recovered),
            rec["recovered"].as_str().unwrap(),
            "recovery {:?}: bytes mismatch",
            rec["note"]
        );
        // The recovered blob must equal the original input.
        assert_eq!(recovered, input, "recovery {:?}: != original input", rec["note"]);
    }
}

/// PARITY #3: only K-1 survivors is unrecoverable (the fixture's should_fail case). Ports MUST treat
/// this as a failure (`Result::Err`).
#[test]
fn reed_solomon_k_minus_one_fails() {
    let v = load_vectors();
    let (k, m, _n, input_size) = params(&v);
    let input = from_hex(v["input"].as_str().unwrap());

    let codec = ReedSolomonCodec::new(k, m).unwrap();
    let shards = codec.encode_data(&input).unwrap();

    let survivors = v["should_fail"]["survivor_indices"].as_array().unwrap();
    assert_eq!(survivors.len(), k - 1, "should_fail must carry K-1 survivors");

    let mut available: BTreeMap<usize, Vec<u8>> = BTreeMap::new();
    for idx in survivors {
        let i = idx.as_u64().unwrap() as usize;
        available.insert(i, shards[i].clone());
    }

    let result = codec.reconstruct_data(&available, input_size);
    assert!(
        matches!(result, Err(VaultError::Unrecoverable(_))),
        "expected K-1 survivors to FAIL decoding, got {result:?}"
    );
}

/// Recovery works from JUST the M parity shards plus enough data shards to reach K — exercising the
/// general matrix-inversion path with the maximum number of parity rows the code can use.
#[test]
fn reed_solomon_parity_assisted_round_trip() {
    let v = load_vectors();
    let (k, m, n, input_size) = params(&v);
    let input = from_hex(v["input"].as_str().unwrap());

    let codec = ReedSolomonCodec::new(k, m).unwrap();
    let shards = codec.encode_data(&input).unwrap();

    // Drop the first M data shards; survive on data[M..K-1] + all M parity shards = K total.
    let mut available: BTreeMap<usize, Vec<u8>> = BTreeMap::new();
    for i in m..k {
        available.insert(i, shards[i].clone());
    }
    for i in k..n {
        available.insert(i, shards[i].clone());
    }

    let recovered = codec.reconstruct_data(&available, input_size).unwrap();
    assert_eq!(recovered, input, "parity-assisted recovery did not reproduce the input");
}
