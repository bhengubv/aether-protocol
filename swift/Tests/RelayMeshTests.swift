// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

/// 3-node mesh-integration proof for circuit-relay-v2: the engine relays A->B through R over
/// real MeshPacket frames (type `.circuitRelayControl`) with NO direct A-B link, surfacing at B
/// via the transport onData callback — exactly how a host mesh consumes it. Mirrors the C#
/// CircuitRelayMeshIntegrationTests and the Go / Python / TS / Rust / Kotlin mesh tests.
///
/// As with RelayEngineTests, all three transports (and the hub) must be *retained* for the
/// test's duration — a RelayTransport holds its link, but the link holds the transport weakly,
/// so a dropped transport is ARC-deallocated and stops handling frames. `withExtendedLifetime`
/// keeps them alive (the DI container / TransportManager owns them in production).
final class RelayMeshTests: XCTestCase {

    /// In-process mesh, adjacency A-R-B with NO direct A-B edge; routes each MeshPacket one hop
    /// to the destination node's link on a fresh thread (stands in for the real radios).
    final class MeshHub {
        private let lock = NSLock()
        private var edges = Set<String>()
        private var links: [String: MeshRelayLink] = [:]

        func connect(_ x: String, _ y: String) {
            lock.lock(); edges.insert("\(x)|\(y)"); edges.insert("\(y)|\(x)"); lock.unlock()
        }
        func adjacent(_ x: String, _ y: String) -> Bool {
            lock.lock(); defer { lock.unlock() }; return edges.contains("\(x)|\(y)")
        }
        func register(_ node: String, _ link: MeshRelayLink) {
            lock.lock(); links[node] = link; lock.unlock()
        }
        func sendFrom(_ node: String) -> (MeshPacket) -> Bool {
            return { [weak self] pkt in
                guard let self = self, self.adjacent(node, pkt.destinationUhid) else { return false }
                self.lock.lock(); let l = self.links[pkt.destinationUhid]; self.lock.unlock()
                if let l = l { Thread.detachNewThread { l.handleIncomingPacket(pkt) } } // async one-hop delivery
                return true
            }
        }
        func canReachFrom(_ node: String) -> (String) -> Bool {
            return { [weak self] other in self?.adjacent(node, other) ?? false }
        }
    }

    func test_relay_works_as_mesh_transport() {
        let hub = MeshHub()
        hub.connect("A", "R")
        hub.connect("R", "B") // deliberately NO A-B edge

        let aLink = MeshRelayLink(localUhid: "A", sendOneHop: hub.sendFrom("A"), canReach: hub.canReachFrom("A"))
        let rLink = MeshRelayLink(localUhid: "R", sendOneHop: hub.sendFrom("R"), canReach: hub.canReachFrom("R"))
        let bLink = MeshRelayLink(localUhid: "B", sendOneHop: hub.sendFrom("B"), canReach: hub.canReachFrom("B"))
        hub.register("A", aLink); hub.register("R", rLink); hub.register("B", bLink)

        let a = RelayTransport(localUhid: "A", link: aLink)
        let r = RelayTransport(localUhid: "R", link: rLink)
        let b = RelayTransport(localUhid: "B", link: bLink)

        var got: (String, Data)?
        let exp = expectation(description: "B receives the relayed message")
        b.setOnData { s, d in got = (s, d); exp.fulfill() }

        XCTAssertFalse(a.isConnected("B"))          // no direct path
        XCTAssertTrue(b.reserve("R"))               // B reserves on the relay
        a.setRoute("B", relay: "R")                 // A learns B is reachable via R

        let payload = Data([0xDE, 0xAD, 0xBE, 0xEF])
        XCTAssertTrue(a.send("B", payload))         // relayed A -> R -> B

        wait(for: [exp], timeout: 3)
        XCTAssertEqual(got?.0, "A")
        XCTAssertEqual(got?.1, payload)
        XCTAssertEqual(r.activeBridgeCount, 1)      // R is genuinely bridging over real packets

        withExtendedLifetime((a, r, b, hub)) {}     // keep all alive (link holds transport weakly)
    }
}
