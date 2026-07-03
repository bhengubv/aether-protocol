// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

/// Gap-2 acceptance proof for **Swift**: the circuit-relay-v2 engine, wrapped as a
/// ``RelayCircuitTransport`` via the ``MeshCircuitRelay/create(localUhid:sendOneHop:canReach:options:)``
/// factory, must be **auto-selected by ``TransportManager`` as the last-resort serverless fallback**
/// — NOT called directly. A and B each run a manager whose ONLY transport is the relay; A sends B's
/// payload *through the manager*, which (additional transports, power cost 90) must pick the relay,
/// and B receives the exact bytes tagged with the relay transport's name. R shows one active bridge,
/// proving a real relayed hop over `MeshPacket` type-57 (`.circuitRelayControl`) frames with NO
/// direct A–B edge. Mirrors the C# `CircuitRelayMeshIntegrationTests` and the Go / Python / TS mesh
/// manager tests.
///
/// As in `RelayMeshTests` / `RelayEngineTests`, every node (and each manager) must be *retained* for
/// the test's duration: a ``RelayTransport`` holds its ``MeshRelayLink``, but the link holds the
/// engine weakly (no retain cycle), so a dropped transport is ARC-deallocated and stops handling
/// frames. In production the DI container / ``TransportManager`` owns them; here `withExtendedLifetime`
/// keeps them alive.
final class CircuitRelayManagerIntegrationTests: XCTestCase {

    /// In-process mesh, adjacency A-R-B with NO direct A-B edge; routes each `MeshPacket` one hop to
    /// the destination node's link on a fresh thread (stands in for the real radios). Identical in
    /// shape to the hub in `RelayMeshTests`.
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

    /// The gap-2 acceptance test: relay is auto-selected by ``TransportManager`` as the fallback.
    func test_relay_is_auto_selected_by_transport_manager_as_fallback() {
        let hub = MeshHub()
        hub.connect("A", "R")
        hub.connect("R", "B") // deliberately NO A-B edge

        // Each node's relay is wired through the factory: (transport, link).
        let (aT, aL) = MeshCircuitRelay.create(localUhid: "A", sendOneHop: hub.sendFrom("A"), canReach: hub.canReachFrom("A"))
        let (rT, rL) = MeshCircuitRelay.create(localUhid: "R", sendOneHop: hub.sendFrom("R"), canReach: hub.canReachFrom("R"))
        let (bT, bL) = MeshCircuitRelay.create(localUhid: "B", sendOneHop: hub.sendFrom("B"), canReach: hub.canReachFrom("B"))
        hub.register("A", aL); hub.register("R", rL); hub.register("B", bL)

        // A and B each run a manager whose ONLY transport is the relay (no BLE/Wi-Fi/NearLink),
        // so if the message arrives it can only be because the manager selected the relay.
        let aMgr = TransportManager(aT)
        let bMgr = TransportManager(bT)

        var got: (sender: String, data: Data, via: String)?
        let exp = expectation(description: "B receives via TransportManager selection")
        bMgr.onDataReceived { sender, data, via in got = (sender, data, via); exp.fulfill() }

        // B advertises reachability by reserving on R; A learns B is reachable via R.
        XCTAssertTrue(bT.reserve("R"))
        aT.setRoute("B", relay: "R")

        let payload = Data([0x11, 0x22, 0x33, 0x44])

        // Send via the MANAGER — which must select the relay (its only, last-resort transport).
        let sendExp = expectation(description: "manager send completes true")
        Task {
            let ok = await aMgr.sendAsync(peerUhid: "B", data: payload)
            XCTAssertTrue(ok, "A manager.sendAsync returned false — the relay was not selected")
            sendExp.fulfill()
        }

        wait(for: [sendExp, exp], timeout: 5)
        XCTAssertEqual(got?.sender, "A")
        XCTAssertEqual(got?.data, payload)
        XCTAssertEqual(got?.via, RelayCircuitTransport.transportName, "manager must tag the selected transport")
        XCTAssertEqual(rT.activeBridgeCount, 1) // R is genuinely bridging over real packets

        withExtendedLifetime((aT, rT, bT, aMgr, bMgr, hub)) {}
    }

    /// Companion proof that the factory-built transport works end-to-end when driven directly
    /// (no manager) — the relay surfaces the payload through its ``RelayCircuitTransport/onDataReceived(_:)``
    /// receive surface. Mirrors the C# `Relay_Works_As_Mesh_Transport_Over_MeshPacket_Frames`.
    func test_relay_transport_delivers_over_mesh_via_factory() {
        let hub = MeshHub()
        hub.connect("A", "R")
        hub.connect("R", "B") // no A-B edge

        let (aT, aL) = MeshCircuitRelay.create(localUhid: "A", sendOneHop: hub.sendFrom("A"), canReach: hub.canReachFrom("A"))
        let (rT, rL) = MeshCircuitRelay.create(localUhid: "R", sendOneHop: hub.sendFrom("R"), canReach: hub.canReachFrom("R"))
        let (bT, bL) = MeshCircuitRelay.create(localUhid: "B", sendOneHop: hub.sendFrom("B"), canReach: hub.canReachFrom("B"))
        hub.register("A", aL); hub.register("R", rL); hub.register("B", bL)

        var got: (String, Data)?
        let exp = expectation(description: "B receives the relayed message")
        bT.onDataReceived { s, d in got = (s, d); exp.fulfill() }

        XCTAssertFalse(aT.isConnected(peerUhid: "B")) // no direct path
        XCTAssertTrue(bT.reserve("R"))                // B reserves on the relay
        aT.setRoute("B", relay: "R")                  // A learns B is reachable via R

        let payload = Data([0xDE, 0xAD, 0xBE, 0xEF])
        let sendExp = expectation(description: "send completes true")
        Task {
            let ok = await aT.sendAsync(peerUhid: "B", data: payload, cancellationToken: nil)
            XCTAssertTrue(ok)
            sendExp.fulfill()
        }

        wait(for: [sendExp, exp], timeout: 5)
        XCTAssertEqual(got?.0, "A")
        XCTAssertEqual(got?.1, payload)
        XCTAssertEqual(rT.activeBridgeCount, 1)

        withExtendedLifetime((aT, rT, bT, hub)) {}
    }

    /// The manager must order transports ascending by power cost, so the cost-90 relay sits LAST
    /// (serverless fallback) — the invariant that makes it a genuine auto-selected fallback rather
    /// than a hand-wired special case.
    func test_manager_orders_relay_last_by_power_cost() {
        let hub = MeshHub()
        let (relayT, _) = MeshCircuitRelay.create(localUhid: "X", sendOneHop: hub.sendFrom("X"), canReach: hub.canReachFrom("X"))
        let cheap = StubCostTransport(name: "Cheap", powerCost: 1)

        // Register relay first to prove the sort — not registration order — decides the ordering.
        let mgr = TransportManager([relayT, cheap])
        let order = mgr.orderedTransports.map { $0.name }
        XCTAssertEqual(order, ["Cheap", RelayCircuitTransport.transportName])
        XCTAssertEqual(mgr.orderedTransports.last?.powerCostRelative, RelayCircuitTransport.powerCostRelay)

        withExtendedLifetime((relayT, mgr, hub)) {}
    }

    /// Minimal available transport used only to prove power-cost ordering; never actually sends.
    private final class StubCostTransport: TransportService, DataReceivingTransport, @unchecked Sendable {
        let name: String
        let powerCostRelative: Int32
        let isAvailable = true
        let maxBandwidthBps: Int64 = 1_000_000
        let maxRangeMeters: Int32 = 100
        let maxConcurrentPeers: Int32 = 10
        var metrics: PerTransportMetrics? { nil }

        init(name: String, powerCost: Int32) { self.name = name; self.powerCostRelative = powerCost }

        func onDataReceived(_ handler: @escaping (String, Data) -> Void) {}
        func sendAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool { false }
        func sendStreamAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool { false }
        func isConnected(peerUhid: String) -> Bool { false }
    }
}
