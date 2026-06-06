// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

final class AetherNetTagTests: XCTestCase {

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
        let tag = try AetherNetTag.fromPublicKey(knownKey)
        XCTAssertEqual(tag.value, knownTag,
            "Expected \(knownTag), got \(tag.value)")
    }

    func testTagFormatIsXXXXXDashXXXXX() throws {
        let tag = try AetherNetTag.fromPublicKey(knownKey)
        let parts = tag.value.split(separator: "-", omittingEmptySubsequences: false)
        XCTAssertEqual(parts.count, 2)
        XCTAssertEqual(parts[0].count, 5)
        XCTAssertEqual(parts[1].count, 5)
    }

    func testTagContainsOnlyCrockfordCharsAndDash() throws {
        let valid = Set("0123456789ABCDEFGHJKMNPQRSTVWXYZ-")
        let tag = try AetherNetTag.fromPublicKey(knownKey)
        for ch in tag.value {
            XCTAssertTrue(valid.contains(ch),
                "Unexpected character '\(ch)' in tag \(tag.value)")
        }
    }

    // MARK: - Round-trip

    func testRoundTrip() throws {
        let original = try AetherNetTag.fromPublicKey(knownKey)
        let reparsed = try AetherNetTag.parse(original.description)
        XCTAssertEqual(original, reparsed)
    }

    func testDescriptionMatchesValue() throws {
        let tag = try AetherNetTag.fromPublicKey(knownKey)
        XCTAssertEqual(tag.description, tag.value)
    }

    func testIsValidTrue() throws {
        let tag = try AetherNetTag.fromPublicKey(knownKey)
        XCTAssertTrue(tag.isValid)
    }

    // MARK: - verify()

    func testVerifyCorrectKeyReturnsTrue() throws {
        XCTAssertTrue(AetherNetTag.verify(knownTag, publicKey: knownKey))
    }

    func testVerifyWrongKeyReturnsFalse() throws {
        let otherKey = makeKey(0xAA)
        XCTAssertFalse(AetherNetTag.verify(knownTag, publicKey: otherKey))
    }

    func testVerifyMalformedTagReturnsFalse() {
        XCTAssertFalse(AetherNetTag.verify("NOT-VALID", publicKey: knownKey))
    }

    // MARK: - parse() accepts

    func testParseWithSeparator() throws {
        let tag = try AetherNetTag.parse(knownTag)
        XCTAssertEqual(tag.value, knownTag)
    }

    func testParseWithoutSeparator() throws {
        let stripped = knownTag.replacingOccurrences(of: "-", with: "")
        let tag = try AetherNetTag.parse(stripped)
        XCTAssertEqual(tag.value, knownTag)
    }

    func testParseLowercase() throws {
        let tag = try AetherNetTag.parse(knownTag.lowercased())
        XCTAssertEqual(tag.value, knownTag)
    }

    func testParseMixedCase() throws {
        // "nRgPr-bQn4H"  →  canonical "NRGPR-BQN4H"
        let mixed = "nRgPr-bQn4H"
        let tag = try AetherNetTag.parse(mixed)
        XCTAssertEqual(tag.value, knownTag)
    }

    func testParseCanonicallyInsertsHyphen() throws {
        // Input without hyphen → output with hyphen at position 5
        let stripped = knownTag.replacingOccurrences(of: "-", with: "")
        let tag = try AetherNetTag.parse(stripped)
        XCTAssertEqual(tag.value.count, 11)
        XCTAssertEqual(tag.value[tag.value.index(tag.value.startIndex, offsetBy: 5)], "-")
    }

    // MARK: - parse() rejects

    func testParseRejectsEmpty() {
        XCTAssertThrowsError(try AetherNetTag.parse("")) { error in
            guard case AetherNetTag.AetherNetTagError.invalidLength(let len) = error else {
                XCTFail("Expected invalidLength, got \(error)")
                return
            }
            XCTAssertEqual(len, 0)
        }
    }

    func testParseRejectsTooShort() {
        XCTAssertThrowsError(try AetherNetTag.parse("ABCDE")) { error in
            guard case AetherNetTag.AetherNetTagError.invalidLength = error else {
                XCTFail("Expected invalidLength, got \(error)")
                return
            }
        }
    }

    func testParseRejectsTooLong() {
        XCTAssertThrowsError(try AetherNetTag.parse("NRGPRBQN4HEXTRA")) { error in
            guard case AetherNetTag.AetherNetTagError.invalidLength = error else {
                XCTFail("Expected invalidLength, got \(error)")
                return
            }
        }
    }

    func testParseRejectsInvalidCharI() {
        // 'I' is excluded from Crockford alphabet
        XCTAssertThrowsError(try AetherNetTag.parse("IRGPR-BQN4H")) { error in
            guard case AetherNetTag.AetherNetTagError.invalidCharacter(let ch) = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
            XCTAssertEqual(ch, "I")
        }
    }

    func testParseRejectsInvalidCharL() {
        XCTAssertThrowsError(try AetherNetTag.parse("LRGPR-BQN4H")) { error in
            guard case AetherNetTag.AetherNetTagError.invalidCharacter = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
        }
    }

    func testParseRejectsInvalidCharO() {
        XCTAssertThrowsError(try AetherNetTag.parse("ORGPR-BQN4H")) { error in
            guard case AetherNetTag.AetherNetTagError.invalidCharacter = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
        }
    }

    func testParseRejectsInvalidCharU() {
        XCTAssertThrowsError(try AetherNetTag.parse("URGPR-BQN4H")) { error in
            guard case AetherNetTag.AetherNetTagError.invalidCharacter = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
        }
    }

    func testParseRejectsSpace() {
        XCTAssertThrowsError(try AetherNetTag.parse(" RGPR-BQN4H")) { error in
            guard case AetherNetTag.AetherNetTagError.invalidCharacter = error else {
                XCTFail("Expected invalidCharacter, got \(error)")
                return
            }
        }
    }

    // MARK: - tryParse()

    func testTryParseValidReturnsTag() {
        XCTAssertNotNil(AetherNetTag.tryParse(knownTag))
        XCTAssertEqual(AetherNetTag.tryParse(knownTag)?.value, knownTag)
    }

    func testTryParseInvalidReturnsNil() {
        XCTAssertNil(AetherNetTag.tryParse("INVALID"))
        XCTAssertNil(AetherNetTag.tryParse(""))
        XCTAssertNil(AetherNetTag.tryParse("IIIII-IIIII"))
    }

    // MARK: - Determinism and uniqueness

    func testSameKeyProducesSameTag() throws {
        let tag1 = try AetherNetTag.fromPublicKey(knownKey)
        let tag2 = try AetherNetTag.fromPublicKey(knownKey)
        XCTAssertEqual(tag1, tag2)
    }

    func testDifferentKeysProduceDifferentTags() throws {
        let tag1 = try AetherNetTag.fromPublicKey(makeKey(0x01))
        let tag2 = try AetherNetTag.fromPublicKey(makeKey(0x02))
        XCTAssertNotEqual(tag1, tag2)
    }

    func testManyDistinctKeys() throws {
        var tags = Set<String>()
        for seed: UInt8 in 0...99 {
            let tag = try AetherNetTag.fromPublicKey(makeKey(seed))
            XCTAssertFalse(tags.contains(tag.value),
                "Collision on seed \(seed): \(tag.value)")
            tags.insert(tag.value)
        }
        XCTAssertEqual(tags.count, 100)
    }

    // MARK: - Invalid key length

    func testFromPublicKeyRejectsTooShort() {
        XCTAssertThrowsError(try AetherNetTag.fromPublicKey([0x01, 0x02])) { error in
            guard case AetherNetTag.AetherNetTagError.invalidPublicKeyLength(let len) = error else {
                XCTFail("Expected invalidPublicKeyLength, got \(error)")
                return
            }
            XCTAssertEqual(len, 2)
        }
    }

    func testFromPublicKeyRejectsTooLong() {
        XCTAssertThrowsError(try AetherNetTag.fromPublicKey([UInt8](repeating: 0, count: 64))) { error in
            guard case AetherNetTag.AetherNetTagError.invalidPublicKeyLength(let len) = error else {
                XCTFail("Expected invalidPublicKeyLength, got \(error)")
                return
            }
            XCTAssertEqual(len, 64)
        }
    }

    func testFromPublicKeyRejectsEmpty() {
        XCTAssertThrowsError(try AetherNetTag.fromPublicKey([])) { error in
            guard case AetherNetTag.AetherNetTagError.invalidPublicKeyLength(let len) = error else {
                XCTFail("Expected invalidPublicKeyLength, got \(error)")
                return
            }
            XCTAssertEqual(len, 0)
        }
    }

    // MARK: - Equatable & Hashable

    func testEquality() throws {
        let a = try AetherNetTag.fromPublicKey(knownKey)
        let b = try AetherNetTag.parse(knownTag)
        XCTAssertEqual(a, b)
    }

    func testHashConsistency() throws {
        let a = try AetherNetTag.fromPublicKey(knownKey)
        let b = try AetherNetTag.parse(knownTag)
        XCTAssertEqual(a.hashValue, b.hashValue)
    }

    func testUsableAsSetElement() throws {
        let a = try AetherNetTag.fromPublicKey(knownKey)
        let b = try AetherNetTag.parse(knownTag)
        let set: Set<AetherNetTag> = [a, b]
        XCTAssertEqual(set.count, 1)
    }

    func testUsableAsDictionaryKey() throws {
        var dict = [AetherNetTag: String]()
        let tag = try AetherNetTag.fromPublicKey(knownKey)
        dict[tag] = "alice"
        XCTAssertEqual(dict[try AetherNetTag.parse(knownTag)], "alice")
    }
}
