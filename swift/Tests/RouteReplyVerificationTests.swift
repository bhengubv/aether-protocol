// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

/// Security acceptance tests for fail-closed RREP verification (Gap 3) — the Swift mirror of the
/// C# `RouteReplyVerificationTests`.
///
/// Proves the properties of the hardened routing layer:
///   (a) a RoutingService with NO verifier supplied REJECTS an RREP — no forward route installed
///       (the default is now `RejectAllRouteReplyVerifier`, fail-closed);
///   (b) an `Ed25519RouteReplyVerifier` whose resolver returns the correct public key ACCEPTS a
///       validly-signed RREP — forward route installed;
///   (c) a forged RREP (signed by a DIFFERENT key), an unsigned RREP, and an unknown-signer RREP
///       are ALL rejected.
///
/// Signed RREPs are built with a REAL Ed25519 keypair via the production signing path
/// (`PacketSigningService.signPacket`). Because Swift CryptoKit's Ed25519 signatures are
/// randomized, we never compare against a fixed signature — we SIGN with a Swift key and then
/// VERIFY (verify-parity is the correct Swift level). Assertions are on the observable side
/// effect: presence / absence of the forward route in the store.
final class RouteReplyVerificationTests: XCTestCase {

    private static let LOCAL = "local-uhid"
    private static let SOURCE = "carol"

    private func newRrep(source: String = SOURCE, dest: String = LOCAL,
                         ttl: Int32 = ProtocolConstants.defaultTtl) -> MeshPacket {
        MeshPacket(type: .routeReply, sourceUhid: source, destinationUhid: dest, ttl: ttl)
    }

    /// Signs an RREP with the given real Ed25519 identity via the production signing path,
    /// filling `signature`. Returns the signed copy.
    private func signRrep(_ rrep: MeshPacket, privateKey: Data, publicKey: Data) async throws -> MeshPacket {
        let signer = PacketSigningService(privateKey: privateKey, publicKey: publicKey)
        var pkt = rrep
        try await signer.signPacket(&pkt)
        return pkt
    }

    // MARK: - (a) No verifier ⇒ fail-closed reject

    func test_noVerifier_rejectsRrep_noRouteInstalled() async {
        let sender = FakeMeshSender(localUhid: Self.LOCAL)
        let store = InMemoryRouteStore()
        // No verifier argument at all — the fail-closed default (RejectAll) must apply.
        let svc = RoutingService(sender: sender, store: store)

        await svc.handleRouteReply(newRrep())

        let route = await store.get(Self.SOURCE)
        XCTAssertNil(route, "route must be rejected — not installed")
        let cached = await svc.getCachedRoute(Self.SOURCE)
        XCTAssertNil(cached)
    }

    // MARK: - (b) Ed25519 verifier + correct key + valid signature ⇒ accept

    func test_ed25519Verifier_validlySignedRrep_installsForwardRoute() async throws {
        let sender = FakeMeshSender(localUhid: Self.LOCAL)
        let store = InMemoryRouteStore()

        // The source node's real identity. Its public key is registered with the resolver.
        let (sourcePriv, sourcePub) = Ed25519Service.generateKeyPair()
        let resolver = StubKeyResolver(uhid: Self.SOURCE, publicKey: sourcePub)

        let verifier = Ed25519RouteReplyVerifier(keyResolver: resolver)
        let svc = RoutingService(sender: sender, store: store, verifier: verifier)

        // Sign with the SOURCE key, then verify against the SOURCE public key (sign-then-verify;
        // no fixed signature, because CryptoKit Ed25519 is randomized).
        let signedRrep = try await signRrep(newRrep(), privateKey: sourcePriv, publicKey: sourcePub)
        await svc.handleRouteReply(signedRrep)

        let route = await store.get(Self.SOURCE)
        XCTAssertNotNil(route)
        XCTAssertEqual(route?.nextHop, Self.SOURCE)
    }

    // MARK: - (c) Forged (wrong-key) signature ⇒ reject

    func test_ed25519Verifier_forgedRrep_signedByDifferentKey_isRejected() async throws {
        let sender = FakeMeshSender(localUhid: Self.LOCAL)
        let store = InMemoryRouteStore()

        // Resolver knows the LEGITIMATE source key...
        let (_, legitPub) = Ed25519Service.generateKeyPair()
        let resolver = StubKeyResolver(uhid: Self.SOURCE, publicKey: legitPub)
        let verifier = Ed25519RouteReplyVerifier(keyResolver: resolver)
        let svc = RoutingService(sender: sender, store: store, verifier: verifier)

        // ...but the attacker signs the RREP (claiming to be "carol") with a DIFFERENT key.
        let (attackerPriv, attackerPub) = Ed25519Service.generateKeyPair()
        let forgedRrep = try await signRrep(newRrep(), privateKey: attackerPriv, publicKey: attackerPub)

        await svc.handleRouteReply(forgedRrep)

        let route = await store.get(Self.SOURCE)
        XCTAssertNil(route, "forged signature must be rejected — no route")
    }

    // MARK: - (c) Unsigned RREP ⇒ reject

    func test_ed25519Verifier_unsignedRrep_isRejected() async {
        let sender = FakeMeshSender(localUhid: Self.LOCAL)
        let store = InMemoryRouteStore()

        let (_, sourcePub) = Ed25519Service.generateKeyPair()
        let resolver = StubKeyResolver(uhid: Self.SOURCE, publicKey: sourcePub)
        let verifier = Ed25519RouteReplyVerifier(keyResolver: resolver)
        let svc = RoutingService(sender: sender, store: store, verifier: verifier)

        // RREP with an empty signature (the MeshPacket default) — must be rejected.
        await svc.handleRouteReply(newRrep())

        let route = await store.get(Self.SOURCE)
        XCTAssertNil(route)
    }

    // MARK: - (c') Unknown signer (resolver returns nil) ⇒ reject

    func test_ed25519Verifier_unknownSource_isRejected() async throws {
        let sender = FakeMeshSender(localUhid: Self.LOCAL)
        let store = InMemoryRouteStore()

        // Resolver knows nobody — even a validly self-signed RREP is rejected (unknown signer).
        let resolver = StubKeyResolver() // empty
        let verifier = Ed25519RouteReplyVerifier(keyResolver: resolver)
        let svc = RoutingService(sender: sender, store: store, verifier: verifier)

        let (sourcePriv, sourcePub) = Ed25519Service.generateKeyPair()
        let signedRrep = try await signRrep(newRrep(), privateKey: sourcePriv, publicKey: sourcePub)

        await svc.handleRouteReply(signedRrep)

        let route = await store.get(Self.SOURCE)
        XCTAssertNil(route)
    }

    /// Minimal in-test UHID→public-key map for the routing verifier.
    private struct StubKeyResolver: RouteReplyKeyResolver {
        private let keys: [String: Data]
        init() { self.keys = [:] }
        init(uhid: String, publicKey: Data) { self.keys = [uhid: publicKey] }
        func resolvePublicKey(_ sourceUhid: String) -> Data? { keys[sourceUhid] }
    }
}
