// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

private let LOCAL = "local-uhid"

private func newRreq(source: String, dest: String, ttl: Int32 = ProtocolConstants.defaultTtl) -> MeshPacket {
    MeshPacket(type: .routeRequest, sourceUhid: source, destinationUhid: dest, ttl: ttl)
}

private func newRrep(source: String, dest: String, ttl: Int32 = ProtocolConstants.defaultTtl) -> MeshPacket {
    MeshPacket(type: .routeReply, sourceUhid: source, destinationUhid: dest, ttl: ttl)
}

final class RoutingServiceTests: XCTestCase {

    // MARK: - HandleRouteRequest

    func test_handleRreq_dropsDuplicateById() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = RoutingService(sender: sender)
        let rreq = newRreq(source: "alice", dest: "bob")
        await svc.handleRouteRequest(rreq)
        sender.clear()
        await svc.handleRouteRequest(rreq)
        XCTAssertEqual(sender.broadcasts().count, 0)
        XCTAssertEqual(sender.unicasts().count, 0)
    }

    func test_handleRreq_ignoresSelfOriginated() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryRouteStore()
        let svc = RoutingService(sender: sender, store: store)
        await svc.handleRouteRequest(newRreq(source: LOCAL, dest: "bob"))
        XCTAssertEqual(sender.broadcasts().count, 0)
        XCTAssertEqual(sender.unicasts().count, 0)
        let all = await store.getAll()
        XCTAssertEqual(all.count, 0)
    }

    func test_handleRreq_installsReverseRouteToSource() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryRouteStore()
        let svc = RoutingService(sender: sender, store: store)
        await svc.handleRouteRequest(newRreq(source: "alice", dest: "bob"))
        let route = await store.get("alice")
        XCTAssertNotNil(route)
        XCTAssertEqual(route?.nextHop, "alice")
        XCTAssertGreaterThanOrEqual(route?.hopCount ?? 0, 1)
    }

    func test_handleRreq_asDestination_sendsRrepBack() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = RoutingService(sender: sender)
        await svc.handleRouteRequest(newRreq(source: "alice", dest: LOCAL))
        let recs = sender.unicasts()
        XCTAssertEqual(recs.count, 1)
        XCTAssertEqual(recs[0].packet.type, .routeReply)
        XCTAssertEqual(recs[0].packet.sourceUhid, LOCAL)
        XCTAssertEqual(recs[0].packet.destinationUhid, "alice")
        XCTAssertEqual(recs[0].nextHopUhid, "alice")
    }

    func test_handleRreq_withCachedRouteToDestination_repliesOnBehalf() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryRouteStore()
        let svc = RoutingService(sender: sender, store: store)
        await store.save(RouteEntry(
            destination: "carol",
            nextHop: "carol",
            hopCount: 1,
            expiresAt: Date(timeIntervalSinceNow: 300),
            qualityScore: 50
        ))
        _ = await svc.findRoute("carol")
        sender.clear()

        await svc.handleRouteRequest(newRreq(source: "alice", dest: "carol"))

        var rrep: MeshPacket? = sender.unicasts().first(where: { $0.packet.type == .routeReply })?.packet
        if rrep == nil {
            rrep = sender.broadcasts().first(where: { $0.type == .routeReply })
        }
        XCTAssertNotNil(rrep, "expected an RREP")
        XCTAssertEqual(rrep?.sourceUhid, "carol")
    }

    func test_handleRreq_forwardsWhenTtlAllows() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = RoutingService(sender: sender)
        await svc.handleRouteRequest(newRreq(source: "alice", dest: "carol", ttl: 5))
        let bcasts = sender.broadcasts()
        XCTAssertEqual(bcasts.count, 1)
        XCTAssertEqual(bcasts[0].ttl, 4)
    }

    func test_handleRreq_dropsWhenTtlExhausted() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = RoutingService(sender: sender)
        await svc.handleRouteRequest(newRreq(source: "alice", dest: "carol", ttl: 1))
        XCTAssertEqual(sender.broadcasts().count, 0)
        XCTAssertEqual(sender.unicasts().count, 0)
    }

    // MARK: - HandleRouteReply

    func test_handleRrep_installsForwardRoute() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryRouteStore()
        // RREP mechanics test: inject the explicit insecure accept-all verifier because
        // the RoutingService default is now fail-closed (RejectAll) and would drop the RREP.
        let svc = RoutingService(sender: sender, store: store, verifier: AcceptAllRouteReplyVerifier())
        await svc.handleRouteReply(newRrep(source: "carol", dest: LOCAL))
        let r = await store.get("carol")
        XCTAssertNotNil(r)
        XCTAssertEqual(r?.nextHop, "carol")
    }

    func test_handleRrep_rejectsWhenVerifierFails() async {
        struct Rejecting: RouteReplyVerifier {
            func verify(_ routeReply: MeshPacket) async -> Bool { false }
        }
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryRouteStore()
        let svc = RoutingService(sender: sender, store: store, verifier: Rejecting())
        await svc.handleRouteReply(newRrep(source: "carol", dest: LOCAL))
        let r = await store.get("carol")
        XCTAssertNil(r)
    }

    func test_handleRrep_forwardsTowardOriginalRequester() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryRouteStore()
        // RREP mechanics test: inject the explicit insecure accept-all verifier because
        // the RoutingService default is now fail-closed (RejectAll) and would drop the RREP.
        let svc = RoutingService(sender: sender, store: store, verifier: AcceptAllRouteReplyVerifier())
        await store.save(RouteEntry(
            destination: "alice",
            nextHop: "bob",
            hopCount: 2,
            expiresAt: Date(timeIntervalSinceNow: 300),
            qualityScore: 50
        ))
        _ = await svc.findRoute("alice")
        sender.clear()

        await svc.handleRouteReply(newRrep(source: "carol", dest: "alice", ttl: 4))

        let fwd = sender.unicasts().first(where: {
            $0.packet.type == .routeReply && $0.nextHopUhid == "bob"
        })
        XCTAssertNotNil(fwd)
        XCTAssertEqual(fwd?.packet.ttl, 3)
    }

    // MARK: - FindRoute / Prune

    func test_findRoute_returnsCachedWithoutBroadcasting() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryRouteStore()
        let svc = RoutingService(sender: sender, store: store)
        await store.save(RouteEntry(
            destination: "bob",
            nextHop: "bob",
            hopCount: 1,
            expiresAt: Date(timeIntervalSinceNow: 300),
            qualityScore: 50
        ))
        let r = await svc.findRoute("bob")
        XCTAssertNotNil(r)
        XCTAssertEqual(r?.nextHop, "bob")
        XCTAssertEqual(sender.broadcasts().count, 0)
    }

    func test_findRoute_returnsNilWhenNoPeers() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = RoutingService(sender: sender)
        let r = await svc.findRoute("bob")
        XCTAssertNil(r)
    }

    func test_pruneAsync_removesExpiredRoutes() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let store = InMemoryRouteStore()
        let svc = RoutingService(sender: sender, store: store)
        await store.save(RouteEntry(
            destination: "stale",
            nextHop: "stale",
            hopCount: 1,
            expiresAt: Date(timeIntervalSinceNow: -10),
            qualityScore: 50
        ))
        await store.save(RouteEntry(
            destination: "fresh",
            nextHop: "fresh",
            hopCount: 1,
            expiresAt: Date(timeIntervalSinceNow: 300),
            qualityScore: 50
        ))
        _ = await svc.findRoute("fresh")
        await svc.prune()
        let stale = await store.get("stale")
        let fresh = await store.get("fresh")
        XCTAssertNil(stale)
        XCTAssertNotNil(fresh)
    }

    // MARK: - Item 19: RREQ-flood reputation hook

    func testRreqFloodFiresReputation() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = RoutingService(sender: sender)
        let rep = NodeReputationService()
        await svc.setReputation(rep)

        // RREQ_RATE_LIMIT_MAX unique packets — still within limit
        for _ in 0..<ProtocolConstants.rreqRateLimitMax {
            let pkt = makeRreq(source: "attacker", dest: "dest")
            await svc.handleRouteRequest(pkt)
        }
        let scoreBefore = await rep.reputationScore(for: "attacker")
        XCTAssertEqual(scoreBefore, 1.0, accuracy: 1e-9)

        // 11th packet — crosses limit, fires hook
        let pkt11 = makeRreq(source: "attacker", dest: "dest")
        await svc.handleRouteRequest(pkt11)
        let scoreAfter = await rep.reputationScore(for: "attacker")
        // After 1 flood record: 1.0 − 0.05 = 0.95
        XCTAssertEqual(scoreAfter, 0.95, accuracy: 1e-9)
    }

    func testRreqNormalTrafficNotPenalised() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = RoutingService(sender: sender)
        let rep = NodeReputationService()
        await svc.setReputation(rep)

        for i in 0..<5 {
            let pkt = makeRreq(source: "node-\(i)", dest: "dest")
            await svc.handleRouteRequest(pkt)
        }
        for i in 0..<5 {
            let score = await rep.reputationScore(for: "node-\(i)")
            XCTAssertEqual(score, 1.0, accuracy: 1e-9)
        }
    }

    func testRreqFloodWithoutReputationNoError() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = RoutingService(sender: sender)  // no reputation

        for _ in 0...ProtocolConstants.rreqRateLimitMax {
            let pkt = makeRreq(source: "attacker", dest: "dest")
            await svc.handleRouteRequest(pkt)
        }
        // No error thrown, no crash
    }

    // MARK: - Helpers

    private func makeRreq(source: String, dest: String) -> MeshPacket {
        MeshPacket(
            type: .routeRequest,
            sourceUhid: source,
            destinationUhid: dest,
            ttl: ProtocolConstants.defaultTtl
        )
    }
}
