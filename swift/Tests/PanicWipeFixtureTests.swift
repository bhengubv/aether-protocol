// SPDX-License-Identifier: MIT

import Crypto
import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language panic-wipe parity: drives the Swift ``PanicWipe`` through the
/// shared fixture (`fixtures/panicwipe/vectors.json`) and asserts the duress-PIN
/// SHA-256 hashes and the canonical key-store name manifest byte-for-byte against
/// the C# reference and every other AetherNet SDK. Also exercises the
/// verify-mismatch path, the short-hash reject, and the secure-erase behaviour.
final class PanicWipeFixtureTests: XCTestCase {

    private struct Vectors: Decodable {
        struct NamePattern: Decodable {
            let index: Int
            let expected: String
        }
        struct DuressPinHash: Decodable {
            let pin: String
            let sha256: String
        }
        let max_prekeys: Int
        let identity_key_names: [String]
        let prekey_name: NamePattern
        let signed_prekey_name: NamePattern
        let duress_pin_hashes: [DuressPinHash]
    }

    /// `#filePath` = .../swift/Tests/PanicWipeFixtureTests.swift → repo root is three levels up.
    private func repoRoot() -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
    }

    private func loadVectors() throws -> Vectors {
        let url = repoRoot().appendingPathComponent("fixtures/panicwipe/vectors.json")
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }

    private func hexEncode(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
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

    // MARK: - Shared fixture (the parity gate)

    func testDuressPinHashVectors() throws {
        let corpus = try loadVectors()
        XCTAssertFalse(corpus.duress_pin_hashes.isEmpty, "expected duress_pin_hashes in the fixture")

        for v in corpus.duress_pin_hashes {
            let hash = PanicWipe.duressPinHash(v.pin)

            // duressPinHash is SHA-256 of the UTF-8 PIN: 32 bytes, byte-for-byte hex.
            XCTAssertEqual(hash.count, 32, "duressPinHash must be 32 bytes for pin \(v.pin.debugDescription)")
            XCTAssertEqual(
                hexEncode(hash), v.sha256,
                "duressPinHash mismatch for pin \(v.pin.debugDescription)")

            // The stored hash verifies; a perturbed PIN does not.
            XCTAssertTrue(
                PanicWipe.verifyDuressPin(v.pin, storedHash: hash),
                "verifyDuressPin must accept the matching PIN \(v.pin.debugDescription)")
            XCTAssertFalse(
                PanicWipe.verifyDuressPin(v.pin + "x", storedHash: hash),
                "verifyDuressPin must reject a perturbed PIN for \(v.pin.debugDescription)")

            // Verifying against the fixture hex (not the freshly computed Data) also holds.
            XCTAssertTrue(
                PanicWipe.verifyDuressPin(v.pin, storedHash: hexDecode(v.sha256)),
                "verifyDuressPin must accept the PIN against the fixture hex for \(v.pin.debugDescription)")
        }
    }

    func testIdentityKeyNames() throws {
        let corpus = try loadVectors()
        XCTAssertEqual(
            PanicWipe.identityKeyNames, corpus.identity_key_names,
            "identityKeyNames must match the fixture manifest (same names, same order)")
    }

    func testMaxPreKeys() throws {
        let corpus = try loadVectors()
        XCTAssertEqual(PanicWipe.maxPreKeys, corpus.max_prekeys)
    }

    func testPreKeyName() throws {
        let corpus = try loadVectors()
        XCTAssertEqual(
            PanicWipe.preKeyName(corpus.prekey_name.index),
            corpus.prekey_name.expected)
    }

    func testSignedPreKeyName() throws {
        let corpus = try loadVectors()
        XCTAssertEqual(
            PanicWipe.signedPreKeyName(corpus.signed_prekey_name.index),
            corpus.signed_prekey_name.expected)
    }

    // MARK: - Behaviour (per-language)

    func testSecureEraseZeroesBuffer() {
        var buffer = Data([1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0xAA, 0x55, 0x00])
        PanicWipe.secureErase(&buffer)
        XCTAssertEqual(buffer.count, 12, "secureErase must not resize the buffer")
        XCTAssertTrue(buffer.allSatisfy { $0 == 0 }, "secureErase must leave the buffer zeroed")
    }

    func testSecureEraseEmptyBufferIsNoOp() {
        var buffer = Data()
        PanicWipe.secureErase(&buffer)
        XCTAssertEqual(buffer.count, 0)
    }

    // MARK: - Reject path

    func testVerifyDuressPinRejectsWrongLengthHash() {
        // A 16-byte hash can never match a 32-byte SHA-256 output → false, no crash.
        let shortHash = Data(repeating: 0, count: 16)
        XCTAssertFalse(PanicWipe.verifyDuressPin("0000", storedHash: shortHash))
    }
}
