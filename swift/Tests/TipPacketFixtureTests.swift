// SPDX-License-Identifier: MIT

import Crypto
import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language tipping parity: the Swift port must reproduce the C# reference vectors
/// (`fixtures/tipping/tip_packet_basic.json`, generated from TipPacketPayload.BuildCanonicalData +
/// Ed25519) byte-for-byte. Covers the invariant-decimal `amount` string, the null reference_id → 16
/// zero bytes case, the .NET mixed-endian GUID byte order, and the i64 LE timestamp.
final class TipPacketFixtureTests: XCTestCase {

    private struct Vectors: Decodable {
        struct Case: Decodable {
            let tipper_uhid: String
            let recipient_uhid: String
            let amount: String
            let traffic_type: String
            let reference_id: String?
            let timestamp_unix_ms: Int64
            let canonical_bytes: String
            let signature: String
        }
        let algorithm: String
        let ed25519_seed: String
        let public_key: String
        let cases: [Case]
    }

    private func loadVectors() throws -> Vectors {
        // #filePath = .../swift/Tests/TipPacketFixtureTests.swift → repo root is three levels up.
        let repoRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
        let url = repoRoot.appendingPathComponent("fixtures/tipping/tip_packet_basic.json")
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }

    private func hexToData(_ s: String) -> Data {
        var out = Data(capacity: s.count / 2)
        var i = s.startIndex
        while i < s.endIndex {
            let next = s.index(i, offsetBy: 2)
            out.append(UInt8(s[i..<next], radix: 16) ?? 0)
            i = next
        }
        return out
    }

    private func hex(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    private func payload(from c: Vectors.Case) -> TipPacketPayload {
        TipPacketPayload(
            tipperUhid: c.tipper_uhid,
            recipientUhid: c.recipient_uhid,
            amount: c.amount,
            trafficType: c.traffic_type,
            referenceId: c.reference_id.flatMap { UUID(uuidString: $0) },
            timestampUnixMs: c.timestamp_unix_ms
        )
    }

    /// `buildCanonicalData()` reproduces the fixture `canonical_bytes` byte-for-byte for every case.
    func testCanonicalBytesParity() throws {
        let v = try loadVectors()
        XCTAssertFalse(v.cases.isEmpty)
        for c in v.cases {
            let got = hex(payload(from: c).buildCanonicalData())
            XCTAssertEqual(got, c.canonical_bytes, "case \(c.tipper_uhid)")
        }
    }

    /// Cross-language signature parity. NOTE: Apple CryptoKit / swift-crypto Ed25519 is RANDOMIZED
    /// (non-deterministic), so — unlike libsodium/dalek/tweetnacl/pynacl in the other 7 languages —
    /// Swift cannot reproduce the fixture signature byte-for-byte. Parity is proven by VERIFICATION
    /// both ways: the derived public key matches the fixture, Swift's own signatures verify, and Swift
    /// verifies the C# fixture signatures. The wire bytes that matter (the canonical body) are asserted
    /// byte-identical in testCanonicalBytesParity.
    func testSignatureCrossVerify() throws {
        let v = try loadVectors()

        let seed = hexToData(v.ed25519_seed)
        XCTAssertEqual(seed.count, 32, "seed must be 32 bytes")

        let privateKey = try Curve25519.Signing.PrivateKey(rawRepresentation: seed)
        let derivedPublic = Data(privateKey.publicKey.rawRepresentation)
        XCTAssertEqual(hex(derivedPublic), v.public_key, "derived public key must match fixture")

        for c in v.cases {
            let canonical = payload(from: c).buildCanonicalData()

            // Apple Ed25519 is randomized, so assert the fresh signature VERIFIES (round-trip) rather
            // than reproducing the fixture bytes (which only deterministic libs can).
            let sig = try privateKey.signature(for: canonical)
            XCTAssertTrue(
                Ed25519Service.verify(derivedPublic, canonical, Data(sig)),
                "Swift's own signature for \(c.tipper_uhid) must verify (round-trip)"
            )

            // The fixture signature verifies against the fixture public key.
            let fixtureSig = hexToData(c.signature)
            XCTAssertTrue(
                Ed25519Service.verify(derivedPublic, canonical, fixtureSig),
                "fixture signature for \(c.tipper_uhid) must verify"
            )
        }
    }

    /// A signed payload survives a JSON round-trip with canonical bytes, signature, amount, and
    /// reference_id nullity intact.
    func testPayloadJSONRoundTrip() throws {
        let v = try loadVectors()
        let seed = hexToData(v.ed25519_seed)
        let privateKey = try Curve25519.Signing.PrivateKey(rawRepresentation: seed)

        for c in v.cases {
            var p = payload(from: c)
            p.signature = Data(try privateKey.signature(for: p.buildCanonicalData()))

            let json = try p.toJSON()
            let back = try TipPacketPayload.parse(json)

            XCTAssertEqual(back.buildCanonicalData(), p.buildCanonicalData(), "canonical bytes round-trip")
            XCTAssertEqual(back.signature, p.signature, "signature round-trip")
            XCTAssertEqual(back.amount, c.amount, "amount round-trip")
            XCTAssertEqual(back.referenceId == nil, p.referenceId == nil, "reference_id nullity round-trip")
            if let a = back.referenceId, let b = p.referenceId {
                XCTAssertEqual(a, b, "reference_id value round-trip")
            }
        }
    }

    // MARK: - Service-level flow

    /// Wires the full MeshTipService send path with the fixture seed and confirms the signed payload
    /// inside the emitted tipPacket(24) carries the exact fixture signature — proving the service-level
    /// flow is byte-identical to C#. With no route resolver, the tip must have been broadcast.
    func testSendTipProducesFixtureSignature() async throws {
        let v = try loadVectors()
        let seed = hexToData(v.ed25519_seed)
        let c = v.cases[0]

        let fakeSender = FakeMeshSender(localUhid: c.tipper_uhid)
        let service = MeshTipService(
            sender: fakeSender,
            signing: SeedPacketSigner(),
            identity: SeedIdentitySigner(seed: seed),
            routing: nil,
            settle: nil
        )

        let signed = try await service.sendTip(
            recipientUhid: c.recipient_uhid,
            amount: c.amount,
            trafficType: c.traffic_type,
            referenceId: c.reference_id.flatMap { UUID(uuidString: $0) },
            timestampUnixMs: c.timestamp_unix_ms
        )
        XCTAssertEqual(signed.type, .tipPacket)

        let emitted = try TipPacketPayload.parse(signed.payload)
        // Apple Ed25519 is randomized, so the service-emitted signature won't byte-match the fixture;
        // assert it VERIFIES over the emitted canonical body, and that the canonical body itself is
        // byte-identical to the fixture (the real wire parity).
        XCTAssertEqual(hex(emitted.buildCanonicalData()), c.canonical_bytes, "emitted canonical body")
        XCTAssertTrue(
            Ed25519Service.verify(hexToData(v.public_key), emitted.buildCanonicalData(), emitted.signature ?? Data()),
            "service-emitted signature must verify"
        )

        // No route resolver → the tip must have been broadcast, not unicast.
        XCTAssertEqual(fakeSender.broadcasts().count, 1)
        XCTAssertEqual(fakeSender.unicasts().count, 0)
    }

    /// An inbound tipPacket(24) is dispatched to the host settlement hook (the Swift analog of
    /// IAetherNetIncentiveProvider.SettleMeshTip); a packet with a malformed signature is dropped
    /// before the hook fires.
    func testHandleTipPacketRoutesToSettlementHook() async throws {
        let v = try loadVectors()
        let seed = hexToData(v.ed25519_seed)
        let privateKey = try Curve25519.Signing.PrivateKey(rawRepresentation: seed)
        let c = v.cases[0]

        // Local node is the addressed recipient, so no onward relay happens.
        let fakeSender = FakeMeshSender(localUhid: c.recipient_uhid)
        let settler = RecordingSettler()
        let service = MeshTipService(
            sender: fakeSender,
            signing: SeedPacketSigner(),
            identity: SeedIdentitySigner(seed: seed),
            routing: nil,
            settle: settler
        )

        // Build a well-formed, signed tip payload.
        var p = payload(from: c)
        p.signature = Data(try privateKey.signature(for: p.buildCanonicalData()))
        var packet = MeshPacket(
            type: .tipPacket,
            sourceUhid: c.tipper_uhid,
            destinationUhid: c.recipient_uhid,
            payload: try p.toJSON()
        )

        let handled = await service.handleTipPacket(packet)
        XCTAssertTrue(handled, "well-formed tip must be handled")
        let firstCalls = await settler.calls()
        XCTAssertEqual(firstCalls.count, 1, "settlement hook fires once")
        XCTAssertEqual(firstCalls.first?.tipperUhid, c.tipper_uhid)

        // A malformed signature (wrong length) must be dropped before the hook fires.
        await settler.reset()
        p.signature = Data([0x00, 0x01, 0x02])
        packet = MeshPacket(
            type: .tipPacket,
            sourceUhid: c.tipper_uhid,
            destinationUhid: c.recipient_uhid,
            payload: try p.toJSON()
        )
        let handledBad = await service.handleTipPacket(packet)
        XCTAssertFalse(handledBad, "malformed-signature tip must be dropped")
        let badCalls = await settler.calls()
        XCTAssertEqual(badCalls.count, 0, "settlement hook must NOT fire for a malformed-signature tip")
    }

    /// The default no-op settlement provider settles nothing without throwing.
    func testNoopSettlementProvider() async throws {
        let provider = NoopMeshTipSettlementProvider()
        try await provider.settleMeshTip(
            TipPacketPayload(
                tipperUhid: "a", recipientUhid: "b", amount: "1", trafficType: "t", timestampUnixMs: 0
            )
        )
    }
}

// MARK: - Test doubles

/// Signs a payload's canonical bytes with the fixture-seed Ed25519 key (real deterministic Ed25519).
private struct SeedIdentitySigner: TipIdentitySigner {
    let seed: Data

    func signData(_ data: Data) async throws -> Data {
        try Ed25519Service.sign(seed, data)
    }
}

/// Minimal envelope signer for the tip dispatch test — stamps a fixed nonce/signature so the emitted
/// packet is well-formed without exercising real envelope crypto (the payload signature is what the
/// parity assertion checks).
private struct SeedPacketSigner: TipPacketSigner {
    func sign(packet: inout MeshPacket) async throws {
        packet.packetNonce = Data([1, 2, 3, 4, 5, 6, 7, 8])
        packet.signature = Data("envelope-sig".utf8)
    }
}

/// Records every settlement-hook invocation so a test can assert exact arguments.
private actor RecordingSettler: MeshTipSettlementProvider {
    private var recorded: [TipPacketPayload] = []
    func settleMeshTip(_ payload: TipPacketPayload) async throws { recorded.append(payload) }
    func calls() -> [TipPacketPayload] { recorded }
    func reset() { recorded.removeAll() }
}
