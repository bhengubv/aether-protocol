// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language ERID parity: the Swift port must reproduce the C# reference vectors
/// (fixtures/erid/vectors.json) byte-for-byte.
final class EridFixtureTests: XCTestCase {

    private struct Vectors: Codable {
        struct EpochErid: Codable { let epoch: Int64; let erid: String }
        struct UnixErid: Codable { let unix: Int64; let erid: String }
        let secret_ascii: String
        let routing_key_hex: String
        let epoch_seconds: Int64
        let erid_length: Int
        let erids_by_epoch: [EpochErid]
        let derive_by_unixseconds: [UnixErid]
        let announcement_encode_hex: String
    }

    private func loadVectors() throws -> Vectors {
        // #filePath = .../swift/Tests/EridFixtureTests.swift → repo root is three levels up.
        let repoRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
        let url = repoRoot.appendingPathComponent("fixtures/erid/vectors.json")
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }

    private func hex(_ bytes: [UInt8]) -> String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }

    func testEridByteParityWithCSharpFixture() throws {
        let v = try loadVectors()

        let rk = try EphemeralRoutingId.deriveRoutingKey(Array(v.secret_ascii.utf8))
        XCTAssertEqual(hex(rk), v.routing_key_hex, "routingKey")

        for e in v.erids_by_epoch {
            XCTAssertEqual(
                try EphemeralRoutingId.deriveForEpoch(rk, epoch: e.epoch, length: v.erid_length),
                e.erid, "epoch \(e.epoch)"
            )
        }
        for e in v.derive_by_unixseconds {
            XCTAssertEqual(
                try EphemeralRoutingId.derive(
                    rk, unixSeconds: e.unix, epochSeconds: v.epoch_seconds, length: v.erid_length
                ),
                e.erid, "unix \(e.unix)"
            )
        }

        let enc = try EridAnnouncementCodec.encode(
            rk, epochSeconds: Int32(v.epoch_seconds), eridLength: Int32(v.erid_length)
        )
        XCTAssertEqual(hex(enc), v.announcement_encode_hex, "announcement frame")

        // Round-trip the frame back through the decoder.
        let dec = try XCTUnwrap(EridAnnouncementCodec.tryDecode(enc))
        XCTAssertEqual(hex(dec.routingKey), v.routing_key_hex)
        XCTAssertEqual(dec.epochSeconds, Int32(v.epoch_seconds))
        XCTAssertEqual(dec.eridLength, Int32(v.erid_length))
    }

    func testEridDirectoryResolveAndOutsider() throws {
        let aKey = try EphemeralRoutingId.deriveRoutingKey(Array("identity-A".utf8))
        let bKey = try EphemeralRoutingId.deriveRoutingKey(Array("identity-B".utf8))
        let alice = try EridDirectory(aKey)
        let bob = try EridDirectory(bKey)
        alice.rememberPeer("bob", routingKey: bKey)
        bob.rememberPeer("alice", routingKey: aKey)
        let t: Int64 = 1_700_000_000

        // An established peer resolves the other's rotating address, both directions.
        XCTAssertEqual(try alice.eridForPeer("bob", unixSeconds: t), try bob.myErid(t))
        XCTAssertEqual(try bob.resolvePeer(alice.myErid(t), unixSeconds: t), "alice")

        // An outsider holding no routing key cannot.
        let outsider = try EridDirectory(
            try EphemeralRoutingId.deriveRoutingKey(Array("identity-X".utf8))
        )
        XCTAssertNil(try outsider.resolvePeer(alice.myErid(t), unixSeconds: t))
    }
}
