// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherMeshProtocol

final class AetherMeshTagTests: XCTestCase {

    // MARK: - Helpers

    /// Deterministic 32-byte key: bytes 0x01 … 0x20.
    private let knownKey: [UInt8] = Array(1...32)

    /// Expected tag for `knownKey`.
    /// Computed: SHA-256([0x01…0x20]) = ae216c2e…
    /// 50-bit word → chars [N,R,G,P,R,B,Q,N,4,H] → "NRGPR-BQN4H"
    private let knownTag = "NRGPR-BQN4H"

    private func makeKey(_ seed: UInt8) -> [UInt8] {
        [UInt8](repeating: seed, count: 32)
    }

    // MARK: - Known vector

    func testKnownVector() throws {
        let tag = try AetherMeshTag.fromPublicKey(knownKey)
        XCTAssertEqual(tag.value, knownTag,
            "Expected \(knownTag), got \(tag.value)")
    }

    func testTagFormatIsXXXXXDashXXXXX() throws {
        let tag = try AetherMeshTag.fromPublicKey(knownKey)
        let parts = tag.value.split(separator: "-", omittingEmptySubsequences: false)
        XCTAssertEqual(parts.count, 2)
        XCTAssertEqual(parts[0].count, 5)
        XCTAssertEqual(parts[1].count, 5)
    }

    func testTagContainsOnlyCrockfordCharsAndDash() throws {
        let valid = Set("0123456789ABCDEFGHJKMNPQRSTVWXYZ-")
        let tag = try AetherMeshTag.fromPublicKey(knownKey)
        for ch in tag.value {
            XCTAssertTrue(valid.contains(ch),
                "Unexpected character '\(ch)' in tag \(tag.value)")
        }
    }

    // MARK: - Round-trip

    func testRoundTrip() throws {
        let original = try AetherMeshTag.fromPublicKey(knownKey)
        let reparsed = try AetherMeshTag.parse(original.description)
        XCTAssertEqual(original, reparsed)
    }

    func testDescriptionMatchesValue() throws {
        let tag = try AetherMeshTag.fromPublicKey(knownKey)
        XCTAssertEqual(tag.description, tag.value)
    }

    func testIsValidTrue() throws {
        let tag = try AetherMeshTag.fromPublicKey(knownKey)
        XCTAssertTrue(tag.isValid)
    }

    // MARK: - verify()

    func testVerifyCorrectKeyReturnsTrue() throws {
        XCTAssertTrue(AetherMeshTag.verify(knownTag, publicKey: knownKey))
    }

    func testVerifyWrongKeyReturnsFalse() throws {
        let otherKey = makeKey(0xAA)
        XCTAssertFalse(AetherMeshTag.verify(knownTag, publicKey: otherKey))
    }

    func testVerifyMalformedTagReturnsFalse() {
        XCTAssertFalse(AetherMeshTag.verify("NOT-VALID", publicKey: knownKey))
    }

    // MARK: - parse() accepts

    func testParseWithSeparator() throws {
        let tag = try AetherMeshTag.parse(knownTag)
        XCTAssertEqual(tag.value, knownTag)
    }

    func testParseWithoutSeparator() throws {
        let stripped = knownTag.replacingOccurrences(of: "-", with: "")
        let tag = try AetherMeshTag.parse(stripped)
        XCTAssertEqual(tag.value, knownTag)
    }

    func testParseLowercase() throws {
        let tag = try AetherMeshTag.parse(knownTag.lowercased())
        XCTAssertEqual(tag.value, knownTag)
    }

    func testParseMixedCase() throws {
        // "nRgPr-bQn4H"  →  canonical "NRGPR-BQN4H"
        let mixed = "nRgPr-bQn4H"
        let tag = try AetherMeshTag.parse(mixed)
        XCTAssertEqual(tag.value, knownTag)
    }

    func testParseCanonicallyInsertsHyphen() throws {
        // Input without hyphen → output with hyphen at position 5
        let stripped = knownTag.replacingOccurrences(of: "-", with: "")
        let tag = try AetherMeshTag.parse(stripped)
        XCTAssertEqual(tag.value.count, 11)
        XCTAssertEqual(tag.value[tag.value.index(tag.value.startIndex, offsetBy: 5)], "-")
    }

    // MARK: - parse() rejects

    func testParseRejectsEmpty() {
        XCTAssertThrowsError(try AetherMeshTag.parse("")) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidLength(let len) = error else {
                XCTFail("Expected invalidLength, got \(error)")
                return
            }
            XCTAssertEqual(len, 0)
        }
    }

    func testParseRejectsTooShort() {
        XCTAssertThrowsError(try AetherMeshTag.parse("ABCDE")) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidLength = error else {
                XCTFail("Expected invalidLength, got \(error)")
                return
            }
        }
    }

    func testParseRejectsTooLong() {
        XCTAssertThrowsError(try AetherMeshTag.parse("NRGPRBQN4HEXTRA")) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidLength = error else {
                XCTFail("Expected invalidLength, got \(error)")
                return
            }
        }
    }

    func testParseRejectsInvalidCharI() {
        // 'I' is excluded from Crockford alphabet
        XCTAssertThrowsError(try AetherMeshTag.parse("IRGPR-BQN4H")) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidCharacter(let ch) = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
            XCTAssertEqual(ch, "I")
        }
    }

    func testParseRejectsInvalidCharL() {
        XCTAssertThrowsError(try AetherMeshTag.parse("LRGPR-BQN4H")) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidCharacter = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
        }
    }

    func testParseRejectsInvalidCharO() {
        XCTAssertThrowsError(try AetherMeshTag.parse("ORGPR-BQN4H")) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidCharacter = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
        }
    }

    func testParseRejectsInvalidCharU() {
        XCTAssertThrowsError(try AetherMeshTag.parse("URGPR-BQN4H")) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidCharacter = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
        }
    }

    func testParseRejectsSpace() {
        XCTAssertThrowsError(try AetherMeshTag.parse(" RGPR-BQN4H")) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidCharacter = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
        }
    }

    // MARK: - tryParse()

    func testTryParseValidReturnsTag() {
        XCTAssertNotNil(AetherMeshTag.tryParse(knownTag))
        XCTAssertEqual(AetherMeshTag.tryParse(knownTag)?.value, knownTag)
    }

    func testTryParseInvalidReturnsNil() {
        XCTAssertNil(AetherMeshTag.tryParse("INVALID"))
        XCTAssertNil(AetherMeshTag.tryParse(""))
        XCTAssertNil(AetherMeshTag.tryParse("IIIII-IIIII"))
    }

    // MARK: - Determinism and uniqueness

    func testSameKeyProducesSameTag() throws {
        let tag1 = try AetherMeshTag.fromPublicKey(knownKey)
        let tag2 = try AetherMeshTag.fromPublicKey(knownKey)
        XCTAssertEqual(tag1, tag2)
    }

    func testDifferentKeysProduceDifferentTags() throws {
        let tag1 = try AetherMeshTag.fromPublicKey(makeKey(0x01))
        let tag2 = try AetherMeshTag.fromPublicKey(makeKey(0x02))
        XCTAssertNotEqual(tag1, tag2)
    }

    func testManyDistinctKeys() throws {
        var tags = Set<String>()
        for seed: UInt8 in 0...99 {
            let tag = try AetherMeshTag.fromPublicKey(makeKey(seed))
            XCTAssertFalse(tags.contains(tag.value),
                "Collision on seed \(seed): \(tag.value)")
            tags.insert(tag.value)
        }
        XCTAssertEqual(tags.count, 100)
    }

    // MARK: - Invalid key length

    func testFromPublicKeyRejectsTooShort() {
        XCTAssertThrowsError(try AetherMeshTag.fromPublicKey([0x01, 0x02])) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidPublicKeyLength(let len) = error else {
                XCTFail("Expected invalidPublicKeyLength, got \(error)")
                return
            }
            XCTAssertEqual(len, 2)
        }
    }

    func testFromPublicKeyRejectsTooLong() {
        XCTAssertThrowsError(try AetherMeshTag.fromPublicKey([UInt8](repeating: 0, count: 64))) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidPublicKeyLength(let len) = error else {
                XCTFail("Expected invalidPublicKeyLength, got \(error)")
                return
            }
            XCTAssertEqual(len, 64)
        }
    }

    func testFromPublicKeyRejectsEmpty() {
        XCTAssertThrowsError(try AetherMeshTag.fromPublicKey([])) { error in
            guard case AetherMeshTag.AetherMeshTagError.invalidPublicKeyLength(let len) = error else {
                XCTFail("Expected invalidPublicKeyLength, got \(error)")
                return
            }
            XCTAssertEqual(len, 0)
        }
    }

    // MARK: - Equatable & Hashable

    func testEquality() throws {
        let a = try AetherMeshTag.fromPublicKey(knownKey)
        let b = try AetherMeshTag.parse(knownTag)
        XCTAssertEqual(a, b)
    }

    func testHashConsistency() throws {
        let a = try AetherMeshTag.fromPublicKey(knownKey)
        let b = try AetherMeshTag.parse(knownTag)
        XCTAssertEqual(a.hashValue, b.hashValue)
    }

    func testUsableAsSetElement() throws {
        let a = try AetherMeshTag.fromPublicKey(knownKey)
        let b = try AetherMeshTag.parse(knownTag)
        let set: Set<AetherMeshTag> = [a, b]
        XCTAssertEqual(set.count, 1)
    }

    func testUsableAsDictionaryKey() throws {
        var dict = [AetherMeshTag: String]()
        let tag = try AetherMeshTag.fromPublicKey(knownKey)
        dict[tag] = "alice"
        XCTAssertEqual(dict[try AetherMeshTag.parse(knownTag)], "alice")
    }
}
