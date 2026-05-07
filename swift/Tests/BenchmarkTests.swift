// SPDX-License-Identifier: MIT

/// XCTest `measure`-block benchmark harness for the Swift aether-protocol
/// hot paths.
///
/// Mirrors the C# Aether.Benchmarks suite, the Go `go/bench` harness, the
/// Python `python/benchmarks/test_benchmark.py`, the C `c/benchmarks/`
/// runner, and the TypeScript `benchmarks/bench.ts` — same eleven hot
/// paths so a regression in any language shows up as a delta against the
/// committed baseline.
///
/// Why XCTest measure blocks? Swift has no canonical micro-benchmark
/// framework the way Go has `testing.B`. `XCTest.measure { … }` is the
/// stdlib-shipped facility and integrates with Xcode's perf-test runner
/// and `swift test` on the command line. It runs each block 10 times by
/// default, reports mean ± stddev, and writes a baseline file you can
/// commit and diff against.
///
/// The eleven cases:
///
///   - `testBench_x25519Agree`             — one ECDH agreement (X3DH inner loop).
///   - `testBench_hkdfSha256_64Bytes`      — KDF_RK per Signal §5.2.
///   - `testBench_x3dhEstablish`           — full pre-key bundle process; 4 X25519 + HKDF.
///   - `testBench_signalEncrypt`           — steady-state Encrypt; HMAC chain + AES-GCM.
///   - `testBench_signalDecrypt`           — steady-state Decrypt.
///   - `testBench_packetSerialize`         — wire serialiser, 50-byte payload.
///   - `testBench_packetSerializeLarge`    — wire serialiser, 10 KB payload.
///   - `testBench_packetDeserialize`       — wire deserialiser.
///   - `testBench_packetRoundTrip`         — Serialize + Deserialize regression detector.
///   - `testBench_routeStoreLookup`        — cached-route hot path.
///   - `testBench_routeStoreSave`          — install a new route entry.
///
/// Run from `swift/`:
///
///   swift test --filter Benchmark
///
/// Pin a baseline by recording in Xcode (Product > Test) once, then
/// diff future runs against the saved measurement. `swift test` prints
/// per-test mean and stddev to stdout; collect with `-Xswiftc -O` for
/// release-mode numbers comparable to the other languages' baselines.
import XCTest
import Crypto
import Foundation
@testable import AetherProtocol

final class BenchmarkTests: XCTestCase {

    // MARK: - Constants

    private static let aliceUhid = "alice-uhid-0001"
    private static let bobUhid = "bob-uhid-0001"
    private static let plaintextSmall = "hello, mesh".data(using: .utf8)!

    /// Tighter measurement options for the cheap primitive benches —
    /// the default 10-iteration block is fine, but we set the metric
    /// explicitly to wall-clock time so the harness reports the same
    /// number on both Apple-silicon and Intel runners.
    private var options: XCTMeasureOptions {
        let opts = XCTMeasureOptions()
        opts.iterationCount = 10
        return opts
    }

    // MARK: - Crypto primitives

    /// One ECDH agreement — the inner-loop primitive of X3DH (4× per
    /// session establishment) and the DH-ratchet step (2× per ratchet).
    func testBench_x25519Agree() {
        let me = Curve25519.KeyAgreement.PrivateKey()
        let peer = Curve25519.KeyAgreement.PrivateKey()
        let peerPub = peer.publicKey
        measure(options: options) {
            for _ in 0..<1000 {
                let s = try! me.sharedSecretFromKeyAgreement(with: peerPub)
                _ = s.withUnsafeBytes { Data($0) }
            }
        }
    }

    /// KDF_RK per Signal §5.2 — 32-byte new root + 32-byte new chain =
    /// 64 bytes out, called once per DH-ratchet step.
    func testBench_hkdfSha256_64Bytes() {
        let ikm = randomBytes(32)
        let salt = randomBytes(32)
        let info = "aether-ratchet-rk-v1".data(using: .utf8)!
        measure(options: options) {
            for _ in 0..<1000 {
                let _ = HKDF<SHA256>.deriveKey(
                    inputKeyMaterial: SymmetricKey(data: ikm),
                    salt: salt,
                    info: info,
                    outputByteCount: 64
                )
            }
        }
    }

    /// Full pre-key bundle process — 4 X25519 + HKDF root derivation.
    /// One-shot per peer; the bench drives a fresh initiator and bundle
    /// every iteration so the session table doesn't grow unbounded.
    func testBench_x3dhEstablish() {
        // Reduce the inner-loop count vs the primitive benches — each
        // iteration creates a new SignalProtocolService + bundle, which
        // is much heavier than a single ECDH agreement.
        measure(options: options) {
            let exp = expectation(description: "x3dh")
            Task {
                for _ in 0..<10 {
                    let bob = SignalProtocolService()
                    let alice = SignalProtocolService()
                    await bob.setLocalUhid(Self.bobUhid)
                    await alice.setLocalUhid(Self.aliceUhid)
                    let bundle = try! await bob.generatePreKeyBundle(localUhid: Self.bobUhid)
                    _ = try! await alice.generatePreKeyBundle(localUhid: Self.aliceUhid)
                    try! await alice.processPreKeyBundle(bundle)
                }
                exp.fulfill()
            }
            wait(for: [exp], timeout: 60)
        }
    }

    // MARK: - Signal Protocol (steady state)

    /// Steady-state Encrypt: 1 HMAC chain step + AES-GCM.
    func testBench_signalEncrypt() async throws {
        let (alice, _) = try await warmedPair()
        measure(options: options) {
            let exp = expectation(description: "encrypt")
            Task {
                for _ in 0..<100 {
                    _ = try! await alice.encrypt(peerUhid: Self.bobUhid, plaintext: Self.plaintextSmall)
                }
                exp.fulfill()
            }
            wait(for: [exp], timeout: 60)
        }
    }

    /// Steady-state Decrypt. Each iteration consumes a freshly-encrypted
    /// payload — the receive ratchet advances on every call, so
    /// re-decrypting the same bytes is invalid.
    func testBench_signalDecrypt() async throws {
        let (alice, bob) = try await warmedPair()
        // Pre-build a batch of encrypted payloads so the measure block
        // only times the Decrypt path.
        var batch: [EncryptedPayload] = []
        batch.reserveCapacity(100)
        for _ in 0..<100 {
            let p = try await alice.encrypt(peerUhid: Self.bobUhid, plaintext: Self.plaintextSmall)
            batch.append(p)
        }
        var idx = 0
        measure(options: options) {
            let exp = expectation(description: "decrypt")
            let start = idx
            idx += 10
            Task {
                for i in 0..<10 where (start + i) < batch.count {
                    _ = try! await bob.decrypt(peerUhid: Self.aliceUhid, payload: batch[start + i])
                }
                exp.fulfill()
            }
            wait(for: [exp], timeout: 60)
        }
    }

    // MARK: - Wire-format serializer

    /// Serialize on a representative 50-byte Data packet. Every packet
    /// on the mesh runs through this on send.
    func testBench_packetSerialize() {
        let pkt = makePacket(payloadSize: 50)
        measure(options: options) {
            for _ in 0..<10000 {
                _ = PacketSerializer.serialize(pkt)
            }
        }
    }

    /// Serialize on a 10 KB payload (typical chunked-data or video-frame
    /// packet). Larger payloads exercise the `Data.append(_:)` path
    /// rather than the per-field header writes.
    func testBench_packetSerializeLarge() {
        let pkt = makePacket(payloadSize: 10240)
        measure(options: options) {
            for _ in 0..<1000 {
                _ = PacketSerializer.serialize(pkt)
            }
        }
    }

    /// Deserialize on a representative wire envelope. Every hop runs
    /// this on receive — a regression multiplies across every router.
    func testBench_packetDeserialize() {
        let wire = PacketSerializer.serialize(makePacket(payloadSize: 50))
        measure(options: options) {
            for _ in 0..<10000 {
                _ = try! PacketSerializer.deserialize(wire)
            }
        }
    }

    /// Combined Serialize + Deserialize. Single-number regression
    /// detector that catches changes in either side. The compiler can't
    /// optimise the deserialize away because we touch a field on the
    /// result.
    func testBench_packetRoundTrip() {
        let pkt = makePacket(payloadSize: 50)
        measure(options: options) {
            for _ in 0..<5000 {
                let wire = PacketSerializer.serialize(pkt)
                let got = try! PacketSerializer.deserialize(wire)
                if got.sourceUhid.isEmpty {
                    XCTFail("unexpected empty packet — kept the optimiser honest")
                }
            }
        }
    }

    // MARK: - Routing

    /// Cached-route hot path — the steady state for every outbound
    /// packet that already has a route.
    func testBench_routeStoreLookup() async {
        let store = InMemoryRouteStore()
        let entry = RouteEntry(
            destination: Self.bobUhid,
            nextHop: "relay-uhid",
            hopCount: 2,
            expiresAt: Date(timeIntervalSinceNow: 3600),
            qualityScore: 90
        )
        await store.save(entry)
        measure(options: options) {
            let exp = expectation(description: "lookup")
            Task {
                for _ in 0..<1000 {
                    let got = await store.get(Self.bobUhid)
                    if got == nil {
                        XCTFail("expected cached route")
                    }
                }
                exp.fulfill()
            }
            wait(for: [exp], timeout: 60)
        }
    }

    /// Install a new route entry — what happens on every successful
    /// RREP arrival.
    func testBench_routeStoreSave() async {
        let store = InMemoryRouteStore()
        let expires = Date(timeIntervalSinceNow: 3600)
        measure(options: options) {
            let exp = expectation(description: "save")
            Task {
                for i in 0..<1000 {
                    let entry = RouteEntry(
                        destination: "dest-\(i)",
                        nextHop: "hop",
                        hopCount: 1,
                        expiresAt: expires,
                        qualityScore: 100
                    )
                    await store.save(entry)
                }
                exp.fulfill()
            }
            wait(for: [exp], timeout: 60)
        }
    }

    // MARK: - Helpers

    /// Build a representative `MeshPacket` of a given payload size.
    private func makePacket(payloadSize: Int) -> MeshPacket {
        return MeshPacket(
            id: UUID(),
            type: .data,
            sourceUhid: Self.aliceUhid,
            destinationUhid: Self.bobUhid,
            ttl: 7,
            priority: 1,
            payload: randomBytes(payloadSize),
            createdAt: Date(),
            signature: randomBytes(64),
            packetNonce: randomBytes(8),
            timestampMs: Int64(Date().timeIntervalSince1970 * 1000),
            protocolVersion: 2
        )
    }

    /// Build an Alice/Bob pair with a fully-primed Double Ratchet so the
    /// encrypt/decrypt benches measure the steady-state chain step
    /// rather than the one-shot X3DH cost.
    private func warmedPair() async throws -> (SignalProtocolService, SignalProtocolService) {
        let alice = SignalProtocolService()
        let bob = SignalProtocolService()
        await alice.setLocalUhid(Self.aliceUhid)
        await bob.setLocalUhid(Self.bobUhid)
        _ = try await alice.generatePreKeyBundle(localUhid: Self.aliceUhid)
        let bobBundle = try await bob.generatePreKeyBundle(localUhid: Self.bobUhid)
        try await alice.processPreKeyBundle(bobBundle)
        // Drive the first PreKey message through so future encrypt /
        // decrypts exercise only the chain step.
        let first = try await alice.encrypt(peerUhid: Self.bobUhid, plaintext: Self.plaintextSmall)
        _ = try await bob.decrypt(peerUhid: Self.aliceUhid, payload: first)
        return (alice, bob)
    }

    private func randomBytes(_ count: Int) -> Data {
        var data = Data(count: count)
        if count > 0 {
            _ = data.withUnsafeMutableBytes { buffer in
                SecRandomCopyBytes(kSecRandomDefault, count, buffer.baseAddress!)
            }
        }
        return data
    }
}
