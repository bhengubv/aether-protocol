// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

final class HandlerManifestTests: XCTestCase {

    // MARK: - Descriptor matching

    func testMatchesRootHandler() {
        let descriptor = HandlerDescriptor(name: "profile")
        let captures = descriptor.match("profile")
        XCTAssertEqual(captures, [:])
    }

    func testMatchesExactSubPath() {
        let descriptor = HandlerDescriptor(name: "profile", pathTemplate: "avatar")
        let captures = descriptor.match("profile/avatar")
        XCTAssertEqual(captures, [:])
    }

    func testCapturesSingleRouteParameter() {
        let descriptor = HandlerDescriptor(name: "content", pathTemplate: "{hash}")
        let captures = descriptor.match("content/sha256-abc")
        XCTAssertEqual(captures, ["hash": "sha256-abc"])
    }

    func testCapturesMultiSegmentRouteParameter() {
        let descriptor = HandlerDescriptor(name: "watch", pathTemplate: "{sessionId}/join")
        let captures = descriptor.match("watch/sess-99/join")
        XCTAssertEqual(captures, ["sessionId": "sess-99"])
    }

    func testReturnsNilOnDifferentSegmentCount() {
        let descriptor = HandlerDescriptor(name: "watch", pathTemplate: "{sessionId}/join")
        XCTAssertNil(descriptor.match("watch/sess-99"))
    }

    func testReturnsNilOnDifferentHandlerName() {
        let descriptor = HandlerDescriptor(name: "watch")
        XCTAssertNil(descriptor.match("stream"))
    }

    // MARK: - Manifest

    func testResolvesRootHandler() throws {
        let manifest = HandlerManifest(
            appId: "aether.media",
            handlers: [
                HandlerDescriptor(name: "profile"),
                HandlerDescriptor(name: "profile", pathTemplate: "avatar"),
                HandlerDescriptor(name: "content", pathTemplate: "{hash}"),
                HandlerDescriptor(name: "watch",   pathTemplate: "{sessionId}/join")
            ]
        )
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        let resolved = manifest.resolve(uri)
        XCTAssertNotNil(resolved)
        XCTAssertEqual(resolved?.0.name, "profile")
        XCTAssertEqual(resolved?.1, [:])
    }

    func testResolvesAvatarOverloadsProfile() throws {
        let manifest = HandlerManifest(
            appId: "aether.media",
            handlers: [
                HandlerDescriptor(name: "profile"),
                HandlerDescriptor(name: "profile", pathTemplate: "avatar")
            ]
        )
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/profile/avatar")
        let resolved = manifest.resolve(uri)
        XCTAssertNotNil(resolved)
        XCTAssertEqual(resolved?.0.pathTemplate, "avatar")
    }

    func testResolvesCaptures() throws {
        let manifest = HandlerManifest(
            appId: "aether.media",
            handlers: [
                HandlerDescriptor(name: "content", pathTemplate: "{hash}")
            ]
        )
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/content/sha256-abc")
        let resolved = manifest.resolve(uri)
        XCTAssertEqual(resolved?.1, ["hash": "sha256-abc"])
    }

    func testReturnsNilForUnknownHandler() throws {
        let manifest = HandlerManifest(
            appId: "aether.media",
            handlers: [HandlerDescriptor(name: "profile")]
        )
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/unknown")
        XCTAssertNil(manifest.resolve(uri))
    }

    func testReturnsNilWhenSegmentCountDiffers() throws {
        let manifest = HandlerManifest(
            appId: "aether.media",
            handlers: [HandlerDescriptor(name: "watch", pathTemplate: "{sessionId}/join")]
        )
        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/watch/sess-99")
        XCTAssertNil(manifest.resolve(uri))
    }

    // MARK: - Router

    func testRouterDispatchesToRegisteredHandler() async throws {
        let descriptor = HandlerDescriptor(name: "profile")
        let manifest = HandlerManifest(appId: "aether.media", handlers: [descriptor])
        let router = AetherUriRouter(manifest: manifest)

        actor Counter {
            var count = 0
            func incr() { count += 1 }
        }
        let counter = Counter()
        try await router.registerHandler(descriptor) { _ in
            await counter.incr()
        }

        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        let invoked = try await router.dispatch(uri)
        XCTAssertTrue(invoked)
        let final = await counter.count
        XCTAssertEqual(final, 1)
    }

    func testRouterReturnsFalseWhenNoHandlerMatches() async throws {
        let descriptor = HandlerDescriptor(name: "profile")
        let manifest = HandlerManifest(appId: "aether.media", handlers: [descriptor])
        let router = AetherUriRouter(manifest: manifest)
        try await router.registerHandler(descriptor) { _ in }

        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/unknown")
        let invoked = try await router.dispatch(uri)
        XCTAssertFalse(invoked)
    }

    func testRouterReturnsFalseWhenNoCallback() async throws {
        let descriptor = HandlerDescriptor(name: "profile")
        let manifest = HandlerManifest(appId: "aether.media", handlers: [descriptor])
        let router = AetherUriRouter(manifest: manifest)

        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        let invoked = try await router.dispatch(uri)
        XCTAssertFalse(invoked)
    }

    func testRouterRejectsUnregisteredDescriptor() async throws {
        let inManifest = HandlerDescriptor(name: "profile")
        let outOfManifest = HandlerDescriptor(name: "stranger")
        let manifest = HandlerManifest(appId: "aether.media", handlers: [inManifest])
        let router = AetherUriRouter(manifest: manifest)

        do {
            try await router.registerHandler(outOfManifest) { _ in }
            XCTFail("Expected throw")
        } catch {
            // expected
        }
    }

    func testRouterDispatchByString() async throws {
        let descriptor = HandlerDescriptor(name: "content", pathTemplate: "{hash}")
        let manifest = HandlerManifest(appId: "aether.media", handlers: [descriptor])
        let router = AetherUriRouter(manifest: manifest)

        actor Capture {
            var hash = ""
            func set(_ s: String) { hash = s }
        }
        let capture = Capture()
        try await router.registerHandler(descriptor) { ctx in
            if let h = ctx.routeParameters["hash"] {
                await capture.set(h)
            }
        }

        let invoked = try await router.dispatch(string: "aether://KXJB7-MN2P4/content/sha256-abc")
        XCTAssertTrue(invoked)
        let result = await capture.hash
        XCTAssertEqual(result, "sha256-abc")
    }

    func testRouterPropagatesHandlerErrors() async throws {
        struct BoomError: Error { }
        let descriptor = HandlerDescriptor(name: "profile")
        let manifest = HandlerManifest(appId: "aether.media", handlers: [descriptor])
        let router = AetherUriRouter(manifest: manifest)
        try await router.registerHandler(descriptor) { _ in
            throw BoomError()
        }

        let uri = try AetherUri.parse("aether://KXJB7-MN2P4/profile")
        do {
            _ = try await router.dispatch(uri)
            XCTFail("Expected error")
        } catch is BoomError {
            // expected
        } catch {
            XCTFail("Expected BoomError, got \(error)")
        }
    }
}
