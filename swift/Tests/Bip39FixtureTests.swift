// SPDX-License-Identifier: MIT

import Crypto
import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language BIP-39 parity: drives the Swift codec through the official
/// Trezor test vectors (`fixtures/bip39/vectors.json`) and asserts entropy →
/// mnemonic → seed byte-for-byte against the C# reference and every other
/// AetherNet SDK. Also exercises identity backup/restore and the reject paths
/// that keep a mistyped phrase from silently reconstructing the wrong identity.
final class Bip39FixtureTests: XCTestCase {

    private struct Vectors: Decodable {
        struct Vector: Decodable {
            let entropy: String
            let mnemonic: String
            let seed: String
        }
        let passphrase: String
        let vectors: [Vector]
    }

    /// `#filePath` = .../swift/Tests/Bip39FixtureTests.swift → repo root is three levels up.
    private func repoRoot() -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
    }

    private func loadVectors() throws -> Vectors {
        let url = repoRoot().appendingPathComponent("fixtures/bip39/vectors.json")
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

    // MARK: - Official Trezor vectors (the parity gate)

    func testAllTrezorVectors() throws {
        let corpus = try loadVectors()
        XCTAssertEqual(corpus.vectors.count, 24, "expected 24 BIP-39 vectors")
        XCTAssertEqual(corpus.passphrase, "TREZOR")

        for v in corpus.vectors {
            let entropy = hexDecode(v.entropy)

            // entropy -> mnemonic
            XCTAssertEqual(
                try Bip39.entropyToMnemonic(entropy), v.mnemonic,
                "entropyToMnemonic mismatch for \(v.entropy)")

            // mnemonic -> entropy (checksum enforced)
            XCTAssertEqual(
                hexEncode(try Bip39.mnemonicToEntropy(v.mnemonic)), v.entropy,
                "mnemonicToEntropy mismatch for \(v.entropy)")

            // mnemonic -> seed (PBKDF2-HMAC-SHA512, passphrase "TREZOR")
            XCTAssertEqual(
                hexEncode(Bip39.mnemonicToSeed(v.mnemonic, passphrase: corpus.passphrase)),
                v.seed,
                "mnemonicToSeed mismatch for \(v.entropy)")

            // every fixture phrase is a well-formed, valid mnemonic
            XCTAssertTrue(Bip39.isValid(v.mnemonic), "isValid should accept \(v.entropy)")
        }
    }

    // MARK: - (a) Identity backup/restore for a known 256-bit seed

    func testIdentityRecoveryPhraseForKnownSeed() throws {
        let entropyHex =
            "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f"
        let expectedPhrase =
            "void come effort suffer camp survey warrior heavy shoot primary clutch " +
            "crush open amazing screen patrol group space point ten exist slush " +
            "involve unfold"
        let seed = hexDecode(entropyHex)

        XCTAssertEqual(try IdentityBackup.toRecoveryPhrase(seed), expectedPhrase)

        let restored = try IdentityBackup.fromRecoveryPhrase(expectedPhrase)
        XCTAssertEqual(hexEncode(restored.privateKey), entropyHex,
                       "restored private key must equal the original seed")
    }

    // MARK: - (b) Random identity round-trips and the restored key still signs

    func testRandomIdentityRoundTripSignsAndVerifies() throws {
        let original = Ed25519Service.generateKeyPair()
        XCTAssertEqual(original.privateKey.count, 32)

        let phrase = try IdentityBackup.toRecoveryPhrase(original.privateKey)
        XCTAssertEqual(phrase.split(separator: " ").count, 24, "identity phrase must be 24 words")

        let restored = try IdentityBackup.fromRecoveryPhrase(phrase)
        XCTAssertEqual(restored.privateKey, original.privateKey, "private key must survive round-trip")
        XCTAssertEqual(restored.publicKey, original.publicKey, "public key must survive round-trip")

        // The restored key is fully functional: sign with it, verify with the restored public key.
        let message = Data("aethernet identity recovery".utf8)
        let signature = try Ed25519Service.sign(restored.privateKey, message)
        XCTAssertTrue(
            Ed25519Service.verify(restored.publicKey, message, signature),
            "restored identity must sign and verify")
    }

    // MARK: - (c) Reject paths — a mistyped phrase must throw, never silently succeed

    func testRejectsBadChecksum() {
        // 24 × "abandon": correct word count, wrong checksum.
        let phrase = Array(repeating: "abandon", count: 24).joined(separator: " ")
        XCTAssertFalse(Bip39.isValid(phrase))
        XCTAssertThrowsError(try Bip39.mnemonicToEntropy(phrase)) { error in
            XCTAssertEqual(error as? Bip39Error, .invalidChecksum)
        }
        XCTAssertThrowsError(try IdentityBackup.fromRecoveryPhrase(phrase))
    }

    func testRejectsUnknownWord() {
        // Valid 24-word skeleton with one impossible word swapped in.
        var words = Array(repeating: "abandon", count: 24)
        words[23] = "notaword"
        let phrase = words.joined(separator: " ")
        XCTAssertFalse(Bip39.isValid(phrase))
        XCTAssertThrowsError(try Bip39.mnemonicToEntropy(phrase)) { error in
            XCTAssertEqual(error as? Bip39Error, .unknownWord("notaword"))
        }
    }

    func testRejectsWrongWordCount() {
        let phrase = "abandon abandon abandon"  // 3 words: not in {12,15,18,21,24}
        XCTAssertFalse(Bip39.isValid(phrase))
        XCTAssertThrowsError(try Bip39.mnemonicToEntropy(phrase)) { error in
            XCTAssertEqual(error as? Bip39Error, .invalidWordCount(3))
        }
    }

    func testToRecoveryPhraseRejectsNon32ByteSeed() {
        XCTAssertThrowsError(try IdentityBackup.toRecoveryPhrase(Data(repeating: 0, count: 16))) { error in
            XCTAssertEqual(error as? Bip39Error, .invalidEntropyLength(16))
        }
    }
}
