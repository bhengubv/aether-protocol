// SPDX-License-Identifier: MIT

import Crypto
import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language Proof-of-Vicinity parity: the Swift port must reproduce the C# reference vectors
/// (`fixtures/market/pov_token_basic.json`, PoVTokenCodec.BuildSignableTokenData + Ed25519)
/// byte-for-byte. Covers all three transports and the .NET DateTime.Ticks i64 LE field, plus the
/// witness→subject countersign flow over packet 43.
final class PoVTokenFixtureTests: XCTestCase {

    private struct Vectors: Decodable {
        struct Case: Decodable {
            let subject_uhid: String
            let timestamp_ticks: Int64
            let transport: String
            let transport_byte: UInt8
            let canonical_body: String
            let witness_signature: String
        }
        let algorithm: String
        let witness_seed: String
        let witness_public_key: String
        let cases: [Case]
    }

    private func loadVectors() throws -> Vectors {
        let repoRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
        let url = repoRoot.appendingPathComponent("fixtures/market/pov_token_basic.json")
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

    private func transport(for byte: UInt8) -> PoVTransportType {
        PoVTransportType(rawValue: byte)!
    }

    /// `buildSignableTokenData` reproduces the fixture `canonical_body` byte-for-byte for every case
    /// (all three transports + the .NET DateTime.Ticks i64 LE field).
    func testCanonicalBodyParity() throws {
        let v = try loadVectors()
        XCTAssertFalse(v.cases.isEmpty)
        for c in v.cases {
            let t = transport(for: c.transport_byte)
            let got = hex(PoVTokenCodec.buildSignableTokenData(
                subjectUhid: c.subject_uhid, timestampTicks: c.timestamp_ticks, transport: t))
            XCTAssertEqual(got, c.canonical_body, "canonical body for \(c.subject_uhid)")
            // Transport enum byte must match the named transport.
            XCTAssertEqual(t.wireName, c.transport, "transport name for \(c.subject_uhid)")
        }
    }

    /// Cross-language witness-signature parity. NOTE: Apple CryptoKit / swift-crypto Ed25519 is
    /// RANDOMIZED (non-deterministic), so Swift cannot reproduce the fixture witness_signature
    /// byte-for-byte (the other 7 languages can). Parity is proven by VERIFICATION both ways: the
    /// derived witness public key matches, Swift's own signature verifies, and Swift verifies the C#
    /// fixture signature. The canonical body is byte-identical (testCanonicalBodyParity).
    func testWitnessSignatureCrossVerify() throws {
        let v = try loadVectors()

        let seed = hexToData(v.witness_seed)
        XCTAssertEqual(seed.count, 32, "seed must be 32 bytes")

        let privateKey = try Curve25519.Signing.PrivateKey(rawRepresentation: seed)
        let derivedPublic = Data(privateKey.publicKey.rawRepresentation)
        XCTAssertEqual(hex(derivedPublic), v.witness_public_key, "derived witness public key")

        for c in v.cases {
            let t = transport(for: c.transport_byte)
            let body = PoVTokenCodec.buildSignableTokenData(
                subjectUhid: c.subject_uhid, timestampTicks: c.timestamp_ticks, transport: t)

            // Apple Ed25519 is randomized — assert the fresh signature VERIFIES rather than matching
            // the fixture bytes.
            let sig = try privateKey.signature(for: body)
            XCTAssertTrue(
                Ed25519Service.verify(derivedPublic, body, Data(sig)),
                "Swift's own witness signature for \(c.subject_uhid) must verify (round-trip)"
            )

            let fixtureSig = hexToData(c.witness_signature)
            XCTAssertTrue(
                Ed25519Service.verify(derivedPublic, body, fixtureSig),
                "fixture witness signature for \(c.subject_uhid) must verify"
            )
        }
    }

    /// A token with a witness signature survives a JSON round-trip with its canonical body, signature,
    /// and transport intact.
    func testTokenJSONRoundTrip() throws {
        let v = try loadVectors()
        let seed = hexToData(v.witness_seed)
        let privateKey = try Curve25519.Signing.PrivateKey(rawRepresentation: seed)

        for c in v.cases {
            let t = transport(for: c.transport_byte)
            let body = PoVTokenCodec.buildSignableTokenData(
                subjectUhid: c.subject_uhid, timestampTicks: c.timestamp_ticks, transport: t)
            let token = PoVToken(
                witnessUhid: "aether:witness:zz",
                subjectUhid: c.subject_uhid,
                timestampTicks: c.timestamp_ticks,
                transportUsed: t,
                witnessSignature: Data(try privateKey.signature(for: body))
            )

            let json = try token.toJSON()
            let back = try PoVToken.parse(json)

            XCTAssertEqual(back.signableData(), token.signableData(), "canonical body round-trip")
            XCTAssertEqual(back.witnessSignature, token.witnessSignature, "witness signature round-trip")
            XCTAssertEqual(back.transportUsed, token.transportUsed, "transport round-trip")
        }
    }

    /// The full-resolution .NET DateTime.Ticks i64 (including the sub-second case 02 value
    /// 638123456789012345, which exceeds 2^32 and is NOT representable exactly in a `Double`/`Date`)
    /// survives a token JSON round-trip byte-for-byte. The canonical body is keyed off this raw `Int64`
    /// — never a `Date` — so signature parity holds at 100ns resolution. (A `Date`-based round-trip is
    /// intentionally NOT asserted: `Date` is a `Double` and physically cannot hold 100ns precision at
    /// these timestamps — see `povTicksToDate`.)
    func testRawTicksSurviveJSONRoundTripLosslessly() throws {
        let v = try loadVectors()
        for c in v.cases {
            let t = transport(for: c.transport_byte)
            let token = PoVToken(
                witnessUhid: "aether:witness:zz",
                subjectUhid: c.subject_uhid,
                timestampTicks: c.timestamp_ticks,
                transportUsed: t
            )
            let back = try PoVToken.parse(try token.toJSON())
            XCTAssertEqual(back.timestampTicks, c.timestamp_ticks, "raw ticks i64 must survive JSON exactly")
            // And the canonical body recomputed from the round-tripped ticks still matches the fixture.
            XCTAssertEqual(hex(back.signableData()), c.canonical_body, "canonical body after ticks round-trip")
        }
    }

    // MARK: - On-mesh exchange flow

    /// Exercises the on-mesh exchange end-to-end: the witness issues a token over packet 43; the
    /// subject verifies the witness Ed25519 signature, counter-signs, and records it; and BOTH
    /// signatures then verify against their respective keys. A replay is rejected by the signer's
    /// nonce dedup.
    func testExchangeFullFlow() async throws {
        let (witnessSeed, witnessPub) = Ed25519Service.generateKeyPair()
        let (subjectSeed, subjectPub) = Ed25519Service.generateKeyPair()

        let witnessUhid = "aether:node:witness"
        let subjectUhid = "aether:node:subject"

        // Witness side.
        let wSender = FakePoVSender(localUhid: witnessUhid)
        let witness = PoVTokenExchangeService(
            sender: wSender,
            signing: PassPacketSigner(seed: witnessSeed),
            identity: RealPoVIdentity(seed: witnessSeed)
        )

        let issued = try await witness.issueToken(subjectUhid: subjectUhid, transport: .ble)
        XCTAssertNotNil(issued, "witness must issue a valid token")
        let sentPackets = wSender.sent()
        XCTAssertEqual(sentPackets.count, 1, "exactly one directed send")
        let exchangePkt = sentPackets[0]
        XCTAssertEqual(exchangePkt.type, .povTokenExchange, "issued packet type")
        XCTAssertEqual(exchangePkt.ttl, 1, "issued packet TTL = 1 (one short-range hop)")

        // Subject side receives the witness's packet.
        let sSender = FakePoVSender(localUhid: subjectUhid)
        let subject = PoVTokenExchangeService(
            sender: sSender,
            signing: PassPacketSigner(seed: subjectSeed),
            identity: RealPoVIdentity(seed: subjectSeed)
        )

        let receivedBox = ReceivedBox()
        await subject.setOnTokenReceived { token in receivedBox.set(token) }

        let accepted = try await subject.handleTokenExchange(
            packet: exchangePkt, senderPublicKey: [UInt8](witnessPub))
        XCTAssertTrue(accepted, "subject must accept a valid witness token")

        guard let received = receivedBox.get() else {
            return XCTFail("onTokenReceived did not fire")
        }

        // BOTH signatures must now verify over the same canonical body.
        let body = received.signableData()
        XCTAssertTrue(
            Ed25519Service.verify(witnessPub, body, received.witnessSignature ?? Data()),
            "witness signature must verify on the accepted token")
        XCTAssertTrue(
            Ed25519Service.verify(subjectPub, body, received.subjectSignature ?? Data()),
            "subject countersignature must verify on the accepted token")

        // Score reflects one unique witness for the subject.
        let score = await subject.getScore(subjectUhid)
        XCTAssertEqual(score.uniqueWitnesses, 1, "one unique witness")

        // Replaying the same packet is rejected by the signer's nonce dedup.
        let replay = try await subject.handleTokenExchange(
            packet: exchangePkt, senderPublicKey: [UInt8](witnessPub))
        XCTAssertFalse(replay, "a replayed PoV exchange packet must be rejected")
    }

    /// Hard invariant: a node must never be able to vouch for itself, and an empty subject is refused.
    func testExchangeRejectsSelfVouchAndEmptySubject() async throws {
        let (seed, _) = Ed25519Service.generateKeyPair()
        let sender = FakePoVSender(localUhid: "aether:node:self")
        let svc = PoVTokenExchangeService(
            sender: sender,
            signing: PassPacketSigner(seed: seed),
            identity: RealPoVIdentity(seed: seed)
        )

        let selfVouch = try await svc.issueToken(subjectUhid: "aether:node:self", transport: .ble)
        XCTAssertNil(selfVouch, "a node must not be able to vouch for itself")

        let emptySubject = try await svc.issueToken(subjectUhid: "", transport: .ble)
        XCTAssertNil(emptySubject, "an empty subject must be refused")

        XCTAssertEqual(sender.sent().count, 0, "no packet should be sent for refused issuances")
    }
}

// MARK: - Test doubles

/// Records directed sends made through ``PoVMeshSender``.
private final class FakePoVSender: PoVMeshSender, @unchecked Sendable {
    let localUhid: String
    private let lock = NSLock()
    private var sentPackets: [MeshPacket] = []

    init(localUhid: String) { self.localUhid = localUhid }

    func send(packet: MeshPacket, subjectUhid: String) async -> Bool {
        lock.lock(); sentPackets.append(packet); lock.unlock()
        return true
    }

    func sent() -> [MeshPacket] {
        lock.lock(); defer { lock.unlock() }
        return sentPackets
    }
}

/// Signs/verifies canonical token bodies with a real Ed25519 identity key (the fixture/random seed).
private struct RealPoVIdentity: PoVIdentitySigner {
    let seed: Data
    func signData(_ data: Data) async throws -> Data { try Ed25519Service.sign(seed, data) }
    func verifySignature(publicKey: [UInt8], data: Data, signature: Data) -> Bool {
        Ed25519Service.verify(Data(publicKey), data, signature)
    }
}

/// Envelope signer that stamps a real Ed25519 signature over `source:dest` with the node's key and
/// enforces nonce replay-dedup (mirroring the C# IPacketSigningService contract). Each issued packet
/// gets a fresh, monotonically-increasing nonce so the first delivery verifies and a byte-identical
/// replay is rejected.
private final class PassPacketSigner: PoVPacketSigner, @unchecked Sendable {
    private let seed: Data
    private let lock = NSLock()
    private var seen: Set<String> = []
    private var counter: UInt8 = 0

    init(seed: Data) { self.seed = seed }

    func sign(packet: inout MeshPacket) async throws {
        lock.lock()
        counter &+= 1
        let nonceByte = counter
        lock.unlock()
        packet.packetNonce = Data(repeating: nonceByte, count: 8)
        packet.signature = try Ed25519Service.sign(seed, Data("\(packet.sourceUhid):\(packet.destinationUhid)".utf8))
    }

    func verify(packet: MeshPacket, senderPublicKey: [UInt8]) async throws -> Bool {
        let nonceHex = packet.packetNonce.map { String(format: "%02x", $0) }.joined()
        let key = "\(packet.sourceUhid):\(nonceHex)"
        lock.lock()
        let replayed = seen.contains(key)
        if !replayed { seen.insert(key) }
        lock.unlock()
        if replayed { return false }
        return Ed25519Service.verify(
            Data(senderPublicKey),
            Data("\(packet.sourceUhid):\(packet.destinationUhid)".utf8),
            packet.signature
        )
    }
}

/// Thread-safe single-slot box for capturing the token delivered to `onTokenReceived`.
private final class ReceivedBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: PoVToken?
    func set(_ token: PoVToken) { lock.lock(); value = token; lock.unlock() }
    func get() -> PoVToken? { lock.lock(); defer { lock.unlock() }; return value }
}
