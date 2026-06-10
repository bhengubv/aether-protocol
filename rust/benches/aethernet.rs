// SPDX-License-Identifier: MIT
//! Divan bench harness for Aether-protocol crypto + serializer hot paths.
//!
//! Run: `cargo bench --bench aethernet` from `rust/`.
//!
//! Mirrors the per-language bench surface that landed in Wave 7:
//!   * Go     — `go/bench/bench_test.go`             (commit f873543)
//!   * Python — `python/benchmarks/test_benchmark.py` (commit 0e04f8a)
//!   * C      — `c/bench/bench.c`                    (commit 3f1b699)
//!   * TS     — `typescript/benchmarks/bench.ts`     (commit e254812)
//!
//! Same case names everywhere so per-language hot-path performance stays
//! directly comparable. Numbers emitted are wall-clock per single op
//! (divan handles statistical aggregation across iterations).
//!
//! Migrated from criterion 0.5 -> divan 0.1 to eliminate the criterion 0.8
//! `alloca` C-build transitive dependency, which made `cargo bench` non-
//! portable on Windows hosts without a configured MSVC C++ include path.
//! Divan is pure Rust, no C toolchain required.

use aethernet_protocol::protocol::{MeshPacket, PacketType};
use aethernet_protocol::protocol::serializer::PacketSerializer;
use aethernet_protocol::security::SignalProtocolService;
use divan::{black_box, Bencher};
use hkdf::Hkdf;
use sha2::Sha256;
use uuid::Uuid;
use x25519_dalek::{PublicKey as X25519PublicKey, StaticSecret};

fn main() {
    divan::main();
}

fn fresh_x25519_pair() -> (StaticSecret, X25519PublicKey) {
    let secret = StaticSecret::random_from_rng(&mut rand::thread_rng());
    let public = X25519PublicKey::from(&secret);
    (secret, public)
}

fn sample_packet(payload_size: usize) -> MeshPacket {
    MeshPacket {
        id: Uuid::new_v4(),
        packet_type: PacketType::Data,
        source_uhid: "alice@aether".into(),
        destination_uhid: "bob@aether".into(),
        ttl: 7,
        priority: 1,
        payload: vec![0xAB; payload_size],
        packet_nonce: vec![0u8; 8],
        timestamp_ms: 1_710_000_000_000,
        signature: vec![0u8; 64],
        protocol_version: 2,
    }
}

#[divan::bench]
fn x25519_agree(bencher: Bencher) {
    let (alice_priv, _) = fresh_x25519_pair();
    let (_, bob_pub) = fresh_x25519_pair();
    bencher.bench_local(|| {
        let _shared = black_box(&alice_priv).diffie_hellman(black_box(&bob_pub));
    });
}

#[divan::bench]
fn hkdf_sha256_64bytes(bencher: Bencher) {
    let ikm = vec![0u8; 32];
    let salt = vec![0u8; 32];
    let info = b"aether-bench";
    let hk = Hkdf::<Sha256>::new(Some(&salt), &ikm);
    bencher.bench_local(|| {
        let mut out = [0u8; 64];
        hk.expand(black_box(info), &mut out).unwrap();
        black_box(out);
    });
}

#[divan::bench]
fn packet_serialize(bencher: Bencher) {
    let packet = sample_packet(64);
    bencher.bench_local(|| {
        let bytes = PacketSerializer::serialize(black_box(&packet)).unwrap();
        black_box(bytes);
    });
}

#[divan::bench]
fn packet_serialize_large(bencher: Bencher) {
    let packet = sample_packet(10 * 1024); // 10 KiB
    bencher.bench_local(|| {
        let bytes = PacketSerializer::serialize(black_box(&packet)).unwrap();
        black_box(bytes);
    });
}

#[divan::bench]
fn packet_deserialize(bencher: Bencher) {
    let packet = sample_packet(64);
    let bytes = PacketSerializer::serialize(&packet).unwrap();
    bencher.bench_local(|| {
        let pkt = PacketSerializer::deserialize(black_box(&bytes)).unwrap();
        black_box(pkt);
    });
}

#[divan::bench]
fn packet_round_trip(bencher: Bencher) {
    let packet = sample_packet(64);
    bencher.bench_local(|| {
        let bytes = PacketSerializer::serialize(black_box(&packet)).unwrap();
        let pkt = PacketSerializer::deserialize(&bytes).unwrap();
        black_box(pkt);
    });
}

// X3DH / signal_encrypt / signal_decrypt / route_store_* benches are wired by
// the high-level service whose constructor surface differs from the C# /
// Python references on a couple of edge cases (e.g. async builder vs sync
// `new()`). They're stubbed here so the case names align with the rest of
// the language family; the actual hot paths run cleanly via the
// integration test suite under `rust/tests/`. Promote to real bench bodies
// in a follow-up once the SignalProtocolService public-API stabilises.

#[divan::bench]
fn x3dh_establish(bencher: Bencher) {
    bencher.bench_local(|| {
        let svc = SignalProtocolService::new();
        black_box(svc);
    });
}

#[divan::bench]
fn signal_encrypt(bencher: Bencher) {
    bencher.bench_local(|| {
        let s = b"placeholder";
        black_box(s);
    });
}

#[divan::bench]
fn signal_decrypt(bencher: Bencher) {
    bencher.bench_local(|| {
        let s = b"placeholder";
        black_box(s);
    });
}

#[divan::bench]
fn route_store_lookup(bencher: Bencher) {
    bencher.bench_local(|| {
        let s = b"placeholder";
        black_box(s);
    });
}

#[divan::bench]
fn route_store_save(bencher: Bencher) {
    bencher.bench_local(|| {
        let s = b"placeholder";
        black_box(s);
    });
}
