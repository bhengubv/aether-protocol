// SPDX-License-Identifier: MIT

import Crypto
import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language BLE tracking-protection parity: drives the Swift
/// ``BlePrivacy`` through the shared fixture (`fixtures/bleprivacy/vectors.json`)
/// and asserts the rotating Service UUID and IRK-based Resolvable Private
/// Address byte-for-byte against the C# reference and every other AetherNet SDK.
/// Also exercises resolution (right IRK resolves, wrong IRK does not), the
/// window arithmetic boundaries, and the 15-byte-IRK reject path.
final class BlePrivacyFixtureTests: XCTestCase {

    private struct Vectors: Decodable {
        struct UuidVector: Decodable {
            let window: Int64
            let uuid: String
        }
        struct RpaVector: Decodable {
            let window: Int64
            let rpa: String
        }
        let rotation_seconds: Int
        let rotation_key: String
        let irk: String
        let wrong_irk: String
        let uuid_vectors: [UuidVector]
        let rpa_vectors: [RpaVector]
    }

    /// `#filePath` = .../swift/Tests/BlePrivacyFixtureTests.swift → repo root is three levels up.
    private func repoRoot() -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
    }

    private func loadVectors() throws -> Vectors {
        let url = repoRoot().appendingPathComponent("fixtures/bleprivacy/vectors.json")
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }

    private func hexDecode(_ hex: String) -> Data {
        var data = Data(capacity: hex.count / 2)
        var idx = hex.startIndex
        while idx < hex.endIndex {
            let next = hex.index(idx, offsetBy: 2)
            data.append(UInt8(hex[idx..<next], radix: 16)!)
            idx = next
        }
        return data
    }

    private func hexEncode(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - Shared fixture (the parity gate)

    func testServiceUuidVectors() throws {
        let corpus = try loadVectors()
        let rotationKey = hexDecode(corpus.rotation_key)
        XCTAssertFalse(corpus.uuid_vectors.isEmpty, "expected uuid_vectors in the fixture")

        for v in corpus.uuid_vectors {
            XCTAssertEqual(
                BlePrivacy.serviceUuid(rotationKey, window: v.window),
                v.uuid,
                "serviceUuid mismatch for window \(v.window)")
        }
    }

    func testResolvableAddressVectors() throws {
        let corpus = try loadVectors()
        let irk = hexDecode(corpus.irk)
        let wrongIrk = hexDecode(corpus.wrong_irk)
        XCTAssertFalse(corpus.rpa_vectors.isEmpty, "expected rpa_vectors in the fixture")

        for v in corpus.rpa_vectors {
            // resolvableAddress(irk, window) == rpa (hex, byte-for-byte)
            let rpa = try BlePrivacy.resolvableAddress(irk, window: v.window)
            XCTAssertEqual(
                hexEncode(rpa), v.rpa,
                "resolvableAddress mismatch for window \(v.window)")

            // the correct IRK resolves its own address
            XCTAssertTrue(
                BlePrivacy.resolveAddress(irk, rpa: rpa),
                "resolveAddress(irk, rpa) must be true for window \(v.window)")

            // a different IRK does not
            XCTAssertFalse(
                BlePrivacy.resolveAddress(wrongIrk, rpa: rpa),
                "resolveAddress(wrongIrk, rpa) must be false for window \(v.window)")

            // resolve also works when the RPA is taken straight from the fixture hex
            XCTAssertTrue(
                BlePrivacy.resolveAddress(irk, rpa: hexDecode(v.rpa)),
                "resolveAddress(irk, fixtureRpa) must be true for window \(v.window)")
        }
    }

    // MARK: - Window arithmetic

    func testRotationSeconds() {
        XCTAssertEqual(BlePrivacy.rotationSeconds, 900)
    }

    func testWindowForBoundaries() {
        XCTAssertEqual(BlePrivacy.windowFor(899), 0, "899s is still window 0")
        XCTAssertEqual(BlePrivacy.windowFor(900), 1, "900s crosses into window 1")
    }

    // MARK: - Reject path

    func testResolvableAddressRejects15ByteIrk() {
        let shortIrk = Data(repeating: 0, count: 15)
        XCTAssertThrowsError(try BlePrivacy.resolvableAddress(shortIrk, window: 0)) { error in
            XCTAssertEqual(error as? BlePrivacyError, .invalidIrkLength(15))
        }
    }

    func testResolveAddressRejectsMalformedInput() {
        // 15-byte IRK and wrong-length RPA both return false rather than throwing.
        let rpa = Data(repeating: 0, count: 6)
        XCTAssertFalse(BlePrivacy.resolveAddress(Data(repeating: 0, count: 15), rpa: rpa))
        XCTAssertFalse(BlePrivacy.resolveAddress(Data(repeating: 0, count: 16), rpa: Data(repeating: 0, count: 5)))
    }
}
