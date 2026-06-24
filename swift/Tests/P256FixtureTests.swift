// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language P-256 ECDSA verify parity: drives `Ed25519Service.verifyWithFallback`
/// through the shared corpus at `tests/cross-language/p256-fixtures.json` — a DER
/// SubjectPublicKeyInfo public key + an ASN.1 DER ECDSA signature over SHA-256
/// (PROTOCOL_SPEC.md §7.5). Every AetherNet SDK drives the SAME vectors; an
/// Ed25519-only regression rejects the valid vector and fails here.
final class P256FixtureTests: XCTestCase {

    private struct Corpus: Codable {
        struct Vector: Codable {
            let name: String
            let public_key_der: String
            let message: String
            let signature_der: String
            let valid: Bool
        }
        let vectors: [Vector]
    }

    private func loadCorpus() throws -> Corpus {
        // #filePath = .../swift/Tests/P256FixtureTests.swift → repo root is three up.
        let repoRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
        let url = repoRoot.appendingPathComponent("tests/cross-language/p256-fixtures.json")
        return try JSONDecoder().decode(Corpus.self, from: Data(contentsOf: url))
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

    func testVerifyWithFallbackDrivesEveryP256Vector() throws {
        let corpus = try loadCorpus()
        XCTAssertFalse(corpus.vectors.isEmpty, "no vectors")
        for v in corpus.vectors {
            let pub = hexDecode(v.public_key_der)
            let msg = hexDecode(v.message)
            let sig = hexDecode(v.signature_der)
            // A >32-byte key forces the P-256 branch; the Ed25519 path takes only 32.
            XCTAssertGreaterThan(pub.count, 32, "\(v.name): P-256 key must be > 32 bytes")
            XCTAssertEqual(
                Ed25519Service.verifyWithFallback(pub, msg, sig), v.valid, v.name
            )
        }
    }
}
