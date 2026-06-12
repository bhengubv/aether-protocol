// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

final class EphemeralRoutingIdTests: XCTestCase {

    // MARK: - Canonical cross-language parity vectors
    //
    // GROUND TRUTH, derived from the C# reference
    // (src/AetherNet.Core/Identity/EphemeralRoutingId.cs). Every language port MUST
    // reproduce these byte-for-byte. Do not edit without regenerating from C#.

    private let routingKeyVectors: [(String, String)] = [
        ("node-secret-A", "206f67e52afa8de0624fd3a2efc5bd68c65879ab623141811c996f0d416345e3"),
        ("node-B", "b071f5176536876b74a8927a242decea37aba390df06ec0019b711122c05384b"),
        ("n", "44874ed0e4e94dc12ea647a9460644feb1495f7dd348e583fcd3c5399388819a"),
    ]

    private let eridVectors: [(String, Int64, String)] = [
        ("node-secret-A", 0, "Q3AN7RWEGZBPZ5WM"),
        ("node-secret-A", 1, "N1HGBC2VC72W0A7E"),
        ("node-secret-A", 100, "KYF9JXYE3XJGFK26"),
        ("node-secret-A", 12345, "ZFM5AZMY6K0TGEK0"),
        ("node-secret-A", 1371, "N080TN3W537B27ZE"),
        ("node-B", 0, "61V5RVS7BVEBTV39"),
        ("node-B", 1, "6NQ731EA0HNGAN3C"),
        ("node-B", 100, "PDEMCT481QBWQN9P"),
        ("node-B", 12345, "H2D11G5JJY5EQ0PW"),
        ("node-B", 1371, "003WA1T3KDQVSDET"),
        ("n", 0, "GGY1T8FKNWCFXS71"),
        ("n", 1, "76AA5GEDFJ669RQS"),
        ("n", 100, "CFSM7DAP0Z1QT2KT"),
        ("n", 12345, "MJT2C0EYGYVRF4KN"),
        ("n", 1371, "39MYY8R0ZA292MPD"),
    ]

    private let crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    private func key(_ secret: String) throws -> [UInt8] {
        try EphemeralRoutingId.deriveRoutingKey(Array(secret.utf8))
    }

    private func hex(_ bytes: [UInt8]) -> String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - Canonical vectors

    func testRoutingKeyMatchesCanonicalVectors() throws {
        for (secret, want) in routingKeyVectors {
            XCTAssertEqual(hex(try key(secret)), want, "routing key for \(secret)")
        }
    }

    func testEridMatchesCanonicalVectors() throws {
        for (secret, epoch, want) in eridVectors {
            let got = try EphemeralRoutingId.deriveForEpoch(try key(secret), epoch: epoch)
            XCTAssertEqual(got, want, "ERID for (\(secret), \(epoch))")
        }
    }

    // MARK: - Behaviour

    func testDeterministicForSameKeyAndEpoch() throws {
        let k = try key("node-secret-A")
        XCTAssertEqual(
            try EphemeralRoutingId.deriveForEpoch(k, epoch: 12345),
            try EphemeralRoutingId.deriveForEpoch(k, epoch: 12345)
        )
    }

    func testRotatesAcrossConsecutiveEpochs() throws {
        let k = try key("node-secret-A")
        XCTAssertNotEqual(
            try EphemeralRoutingId.deriveForEpoch(k, epoch: 100),
            try EphemeralRoutingId.deriveForEpoch(k, epoch: 101)
        )
    }

    func testDiffersByNodeInSameEpoch() throws {
        XCTAssertNotEqual(
            try EphemeralRoutingId.deriveForEpoch(try key("node-A"), epoch: 7),
            try EphemeralRoutingId.deriveForEpoch(try key("node-B"), epoch: 7)
        )
    }

    func testLengthAndAlphabet() throws {
        let id = try EphemeralRoutingId.deriveForEpoch(try key("n"), epoch: 1)
        XCTAssertEqual(id.count, EphemeralRoutingId.defaultLength)
        for ch in id {
            XCTAssertTrue(crockford.contains(ch), "char \(ch) not in alphabet")
        }
    }

    func testEpochFor() throws {
        let cases: [(Int64, Int64, Int64)] = [
            (0, 900, 0),
            (899, 900, 0),
            (900, 900, 1),
            (1800, 900, 2),
            (1234567, 900, 1371),
            (-50, 900, 0), // negative clamps to 0
        ]
        for (u, e, want) in cases {
            XCTAssertEqual(try EphemeralRoutingId.epochFor(u, epochSeconds: e), want, "epochFor(\(u), \(e))")
        }
    }

    func testStableWithinWindowChangesAtBoundary() throws {
        let k = try key("n")
        XCTAssertEqual(
            try EphemeralRoutingId.derive(k, unixSeconds: 1000),
            try EphemeralRoutingId.derive(k, unixSeconds: 1500)
        )
        XCTAssertNotEqual(
            try EphemeralRoutingId.derive(k, unixSeconds: 1000),
            try EphemeralRoutingId.derive(k, unixSeconds: 2000)
        )
    }

    func testRoutingKeyDeterministic256BitDistinctFromSeed() throws {
        let seed = Array("ed25519-private-key-material-seed".utf8)
        let k1 = try EphemeralRoutingId.deriveRoutingKey(seed)
        let k2 = try EphemeralRoutingId.deriveRoutingKey(seed)
        XCTAssertEqual(k1, k2)
        XCTAssertEqual(k1.count, 32)
        XCTAssertNotEqual(k1, seed)
        XCTAssertNotEqual(try EphemeralRoutingId.deriveRoutingKey(Array("a-different-identity".utf8)), k1)
    }

    func testRejectsEmptyInputs() {
        XCTAssertThrowsError(try EphemeralRoutingId.deriveRoutingKey([]))
        XCTAssertThrowsError(try EphemeralRoutingId.deriveForEpoch([], epoch: 1))
    }
}
