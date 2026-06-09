// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

final class AetherUriTests: XCTestCase {

    // MARK: - Scheme + authority

    func testParsesAuthorityOnly() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4")
        XCTAssertEqual(uri.authority, "KXJB7-MN2P4")
        XCTAssertEqual(uri.path, "")
        XCTAssertEqual(uri.handlerName, "")
        XCTAssertEqual(uri.pathSegments, [])
        XCTAssertEqual(uri.query, [:])
        XCTAssertEqual(uri.fragment, "")
    }

    func testAuthorityWithoutDashCanonicalises() throws {
        let uri = try AetherUri.parse("aether://KXJB7MN2P4")
        XCTAssertEqual(uri.authority, "KXJB7-MN2P4")
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4")
    }

    func testAuthorityLowercaseCanonicalises() throws {
        let uri = try AetherUri.parse("aether://kxjb7-mn2p4")
        XCTAssertEqual(uri.authority, "KXJB7-MN2P4")
    }

    func testSchemeIsCaseInsensitive() throws {
        let uri = try AetherUri.parse("AETHER://KXJB7-MN2P4/profile")
        XCTAssertEqual(uri.authority, "KXJB7-MN2P4")
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/profile")
    }

    func testUhid64HexAuthority() throws {
        let hex64 = String(repeating: "a", count: 64)
        let uri = try AetherUri.parse("aether://\(hex64)/inbox")
        XCTAssertEqual(uri.authority, hex64.uppercased())
        XCTAssertEqual(uri.path, "inbox")
        XCTAssertEqual(uri.handlerName, "inbox")
    }

    // MARK: - Path

    func testSingleSegmentPath() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        XCTAssertEqual(uri.path, "profile")
        XCTAssertEqual(uri.handlerName, "profile")
        XCTAssertEqual(uri.pathSegments, ["profile"])
    }

    func testTwoSegmentPath() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/content/sha256-abc123")
        XCTAssertEqual(uri.path, "content/sha256-abc123")
        XCTAssertEqual(uri.handlerName, "content")
        XCTAssertEqual(uri.pathSegments, ["content", "sha256-abc123"])
    }

    func testPercentEncodedPathSegment() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/inbox/Hello%20World")
        XCTAssertEqual(uri.path, "inbox/Hello World")
        XCTAssertEqual(uri.pathSegments, ["inbox", "Hello World"])
        // Re-encoded canonical form preserves the percent encoding.
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/inbox/Hello%20World")
    }

    // MARK: - Query

    func testQueryParameters() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/content/abc?codec=opus&bitrate=128")
        XCTAssertEqual(uri.query["codec"], "opus")
        XCTAssertEqual(uri.query["bitrate"], "128")
    }

    func testFlagQueryHasEmptyValue() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/x?flag")
        XCTAssertEqual(uri.query["flag"], "")
        // Canonical form does NOT emit a trailing '='.
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/x?flag")
    }

    func testPercentEncodedQuerySpace() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/inbox?title=hello%20world")
        XCTAssertEqual(uri.query["title"], "hello world")
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/inbox?title=hello%20world")
    }

    func testPercentEncodedUtf8() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/inbox?title=caf%C3%A9")
        XCTAssertEqual(uri.query["title"], "café")
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/inbox?title=caf%C3%A9")
    }

    func testQueryKeysCaseInsensitiveLookup() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/x?Codec=opus")
        XCTAssertEqual(uri.queryValue(forKey: "codec"), "opus")
        XCTAssertEqual(uri.queryValue(forKey: "CODEC"), "opus")
        XCTAssertEqual(uri.queryValue(forKey: "Codec"), "opus")
    }

    // MARK: - Fragment

    func testFragment() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/stream/live#t=1m30s")
        XCTAssertEqual(uri.fragment, "t=1m30s")
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/stream/live#t=1m30s")
    }

    func testQueryAndFragment() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/x?a=b#frag")
        XCTAssertEqual(uri.query["a"], "b")
        XCTAssertEqual(uri.fragment, "frag")
    }

    func testFragmentWithEqualsNotEncoded() throws {
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/x#t=1m30s")
        XCTAssertEqual(uri.fragment, "t=1m30s")
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/x#t=1m30s")
    }

    // MARK: - Round-trip

    func testRoundTripsAllValidFixtureShapes() throws {
        let inputs = [
            "aether://KXJB7-MN2P4",
            "aether://KXJB7-MN2P4/profile",
            "aether://KXJB7-MN2P4/content/sha256-abc123",
            "aether://KXJB7-MN2P4/content/abc?codec=opus&bitrate=128",
            "aether://KXJB7-MN2P4/stream/live#t=1m30s",
            "aether://KXJB7-MN2P4/x?a=b#frag",
            "aether://KXJB7-MN2P4/x?flag"
        ]
        for s in inputs {
            let uri = try AetherUri.parse(s)
            XCTAssertEqual(uri.description, s, "Round-trip failed for \(s)")
        }
    }

    // MARK: - Invalid inputs

    func testRejectsEmpty() {
        XCTAssertThrowsError(try AetherUri.parse(""))
    }

    func testRejectsWrongScheme() {
        XCTAssertThrowsError(try AetherUri.parse("http://KXJB7-MN2P4/"))
    }

    func testRejectsMissingSlashSlash() {
        XCTAssertThrowsError(try AetherUri.parse("aether:KXJB7-MN2P4"))
    }

    func testRejectsSingleSlash() {
        XCTAssertThrowsError(try AetherUri.parse("aether:/KXJB7-MN2P4"))
    }

    func testRejectsEmptyAuthority() {
        XCTAssertThrowsError(try AetherUri.parse("aether:///profile"))
    }

    func testRejectsNonCrockfordAuthority() {
        XCTAssertThrowsError(try AetherUri.parse("aether://INVALID-AUTH1/x"))
    }

    func testRejectsTooShortAuthority() {
        XCTAssertThrowsError(try AetherUri.parse("aether://ABC"))
    }

    func testRejectsConsecutiveSlashesInPath() {
        XCTAssertThrowsError(try AetherUri.parse("aether://KXJB7-MN2P4/a//b"))
    }

    func testRejectsSpaceInPath() {
        XCTAssertThrowsError(try AetherUri.parse("aether://KXJB7-MN2P4/has space"))
    }

    func testRejectsMalformedPercentEncoding() {
        XCTAssertThrowsError(try AetherUri.parse("aether://KXJB7-MN2P4/inbox/%2"))
    }

    func testRejectsEmptyQueryKey() {
        XCTAssertThrowsError(try AetherUri.parse("aether://KXJB7-MN2P4/x?=value"))
    }

    // MARK: - tryParse

    func testTryParseSuccess() {
        let result = AetherUri.tryParse("aether://KXJB7-MN2P4/profile")
        switch result {
        case .success(let uri):
            XCTAssertEqual(uri.authority, "KXJB7-MN2P4")
        case .failure(let err):
            XCTFail("Expected success, got \(err)")
        }
    }

    func testTryParseFailure() {
        let result = AetherUri.tryParse("invalid")
        switch result {
        case .success:
            XCTFail("Expected failure")
        case .failure:
            break
        }
    }

    // MARK: - Equatable & Hashable

    func testEquality() throws {
        let a = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        let b = try AetherUri.parse("aether://kxjb7-mn2p4/profile")
        XCTAssertEqual(a, b)
    }

    func testInequality() throws {
        let a = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        let b = try AetherUri.parse("aether://KXJB7-MN2P4/content/abc")
        XCTAssertNotEqual(a, b)
    }

    func testHashConsistencyForEqualUris() throws {
        let a = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        let b = try AetherUri.parse("aether://kxjb7-mn2p4/profile")
        XCTAssertEqual(a.hashValue, b.hashValue)
    }

    func testUsableAsSetElement() throws {
        let a = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        let b = try AetherUri.parse("aether://kxjb7-mn2p4/profile")
        let set: Set<AetherUri> = [a, b]
        XCTAssertEqual(set.count, 1)
    }

    // MARK: - Scheme constant

    func testSchemeConstant() {
        XCTAssertEqual(AetherUri.scheme, "aether")
    }
}
