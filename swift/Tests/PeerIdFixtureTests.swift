// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language PeerID parity: the Swift port must reproduce the real js-libp2p reference vectors
/// (fixtures/peerid/inputs.json + fixtures/peerid/expected/<name>.txt) character-for-character.
final class PeerIdFixtureTests: XCTestCase {

    private struct Input: Codable {
        let name: String
        let pubkey_hex: String
    }

    /// `#filePath` = .../swift/Tests/PeerIdFixtureTests.swift → repo root is three levels up.
    private func repoRoot() -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
    }

    private func loadInputs() throws -> [Input] {
        let url = repoRoot().appendingPathComponent("fixtures/peerid/inputs.json")
        return try JSONDecoder().decode([Input].self, from: Data(contentsOf: url))
    }

    private func loadExpected(_ name: String) throws -> String {
        let url = repoRoot().appendingPathComponent("fixtures/peerid/expected/\(name).txt")
        return try String(contentsOf: url, encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func decodeHex(_ hex: String) -> [UInt8] {
        var bytes = [UInt8]()
        bytes.reserveCapacity(hex.count / 2)
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            bytes.append(UInt8(hex[index..<next], radix: 16)!)
            index = next
        }
        return bytes
    }

    func testPeerIdParityWithJsLibp2pFixture() throws {
        let inputs = try loadInputs()
        XCTAssertEqual(inputs.count, 5, "expected 5 fixture cases")

        for input in inputs {
            let pubkey = decodeHex(input.pubkey_hex)
            let expected = try loadExpected(input.name)
            let actual = try PeerId.fromEd25519PublicKey(pubkey)

            XCTAssertEqual(actual, expected, "PeerID mismatch for \(input.name)")
            XCTAssertTrue(actual.hasPrefix("12D3Koo"), "\(input.name) must start with 12D3Koo, got \(actual)")
        }
    }

    func testRejectsWrongLengthKey() {
        XCTAssertThrowsError(try PeerId.fromEd25519PublicKey([UInt8](repeating: 0, count: 31))) { error in
            XCTAssertEqual(error as? PeerId.PeerIdError, .invalidPublicKeyLength(31))
        }
        XCTAssertThrowsError(try PeerId.fromEd25519PublicKey([UInt8](repeating: 0, count: 33))) { error in
            XCTAssertEqual(error as? PeerId.PeerIdError, .invalidPublicKeyLength(33))
        }
    }
}
