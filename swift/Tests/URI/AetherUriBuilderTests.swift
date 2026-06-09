// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

final class AetherUriBuilderTests: XCTestCase {

    // MARK: - Authority

    func testBuildsFromAuthorityString() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .build()
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4")
    }

    func testBuildsFromAetherNetTag() throws {
        let tag = try AetherNetTag.parse("KXJB7-MN2P4")
        let uri = try AetherUriBuilder()
            .authority(tag)
            .build()
        XCTAssertEqual(uri.authority, "KXJB7-MN2P4")
    }

    func testCanonicalisesAuthorityCase() throws {
        let uri = try AetherUriBuilder()
            .authority("kxjb7mn2p4")
            .build()
        XCTAssertEqual(uri.authority, "KXJB7-MN2P4")
    }

    func testThrowsOnEmptyAuthority() {
        XCTAssertThrowsError(try AetherUriBuilder().authority("").build())
    }

    func testThrowsOnMissingAuthority() {
        XCTAssertThrowsError(try AetherUriBuilder().build())
    }

    // MARK: - Path

    func testWithPathStripsLeadingSlash() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("/profile")
            .build()
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/profile")
    }

    func testWithPathMultipleSegments() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("content/sha256-abc")
            .build()
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/content/sha256-abc")
    }

    // MARK: - appendSegment

    func testAppendSegmentBuildsPath() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .appendSegment("content")
            .appendSegment("abc")
            .build()
        XCTAssertEqual(uri.path, "content/abc")
    }

    func testAppendSegmentStripsLeadingSlash() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .appendSegment("/content")
            .appendSegment("/abc")
            .build()
        XCTAssertEqual(uri.path, "content/abc")
    }

    func testAppendSegmentSkipsEmpty() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .appendSegment("content")
            .appendSegment("")
            .appendSegment("abc")
            .build()
        XCTAssertEqual(uri.path, "content/abc")
    }

    // MARK: - Query

    func testWithQueryParam() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("content/abc")
            .query("codec", "opus")
            .build()
        XCTAssertEqual(uri.query["codec"], "opus")
    }

    func testQueryPreservesInsertionOrder() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("x")
            .query("codec", "opus")
            .query("bitrate", "128")
            .build()
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/x?codec=opus&bitrate=128")
    }

    func testRemoveQueryParam() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("x")
            .query("codec", "opus")
            .removeQuery("codec")
            .build()
        XCTAssertTrue(uri.query.isEmpty)
    }

    func testThrowsOnEmptyQueryKey() {
        let builder = AetherUriBuilder()
        XCTAssertThrowsError(try builder.query("", "value"))
    }

    // MARK: - Fragment

    func testWithFragment() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("stream/live")
            .fragment("t=1m30s")
            .build()
        XCTAssertEqual(uri.fragment, "t=1m30s")
        XCTAssertEqual(uri.description, "aether://KXJB7-MN2P4/stream/live#t=1m30s")
    }

    func testFragmentStripsLeadingHash() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("x")
            .fragment("#frag")
            .build()
        XCTAssertEqual(uri.fragment, "frag")
    }

    // MARK: - Full

    func testFullBuilderRoundTrips() throws {
        let uri = try AetherUriBuilder()
            .authority("KXJB7-MN2P4")
            .path("content/sha256-abc123")
            .query("codec", "opus")
            .fragment("t=1m30s")
            .build()
        XCTAssertEqual(
            uri.description,
            "aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s"
        )
    }
}
