// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherNetProtocol

/// Behavioural proof of the native circuit-relay-v2 engine: a three-node topology
/// where A and B can each reach relay R but NOT each other directly. A message from A
/// must traverse the relay bridge to reach B. Mirrors the C#/Go engine tests.
final class RelayEngineTests: XCTestCase {

    // ── in-process one-hop mesh ──────────────────────────────────────────────

    final class InProcMesh {
        private let lock = NSLock()
        private var edges = Set<String>()
        private var links: [String: InProcLink] = [:]

        func connect(_ x: String, _ y: String) {
            lock.lock(); edges.insert("\(x)|\(y)"); edges.insert("\(y)|\(x)"); lock.unlock()
        }
        func adjacent(_ x: String, _ y: String) -> Bool {
            lock.lock(); defer { lock.unlock() }; return edges.contains("\(x)|\(y)")
        }
        func link(_ node: String) -> InProcLink {
            lock.lock(); defer { lock.unlock() }
            if let l = links[node] { return l }
            let l = InProcLink(mesh: self, node: node); links[node] = l; return l
        }
        func deliver(from: String, to: String, frame: Data) {
            guard adjacent(from, to) else { return }
            let l = link(to)
            DispatchQueue.global().async { l.fire(from: from, frame: frame) } // async hop
        }
    }

    final class InProcLink: RelayLink {
        let mesh: InProcMesh
        let node: String
        private let lock = NSLock()
        private var handler: ((String, Data) -> Void)?
        init(mesh: InProcMesh, node: String) { self.mesh = mesh; self.node = node }
        func sendFrame(_ n: String, _ frame: Data) -> Bool {
            guard mesh.adjacent(node, n) else { return false }
            mesh.deliver(from: node, to: n, frame: frame); return true
        }
        func canReach(_ n: String) -> Bool { mesh.adjacent(node, n) }
        func onFrame(_ h: @escaping (String, Data) -> Void) { lock.lock(); handler = h; lock.unlock() }
        func fire(from: String, frame: Data) { lock.lock(); let h = handler; lock.unlock(); h?(from, frame) }
    }

    final class TestClock {
        private let lock = NSLock()
        private var t = Date(timeIntervalSince1970: 1_767_225_600) // fixed 2026-01-01
        func now() -> Date { lock.lock(); defer { lock.unlock() }; return t }
        func advance(_ s: TimeInterval) { lock.lock(); t = t.addingTimeInterval(s); lock.unlock() }
    }

    final class Counter {
        private let lock = NSLock(); private var n = 0
        func inc() -> Int { lock.lock(); defer { lock.unlock() }; n += 1; return n }
    }

    private func buildLine(relayOpts: RelayTransportOptions = .init(),
                           relayClock: (() -> Date)? = nil)
    -> (a: RelayTransport, r: RelayTransport, b: RelayTransport) {
        let m = InProcMesh()
        m.connect("A", "R"); m.connect("R", "B") // NO A-B edge
        let a = RelayTransport(localUhid: "A", link: m.link("A"))
        let r = RelayTransport(localUhid: "R", link: m.link("R"), options: relayOpts, now: relayClock ?? { Date() })
        let b = RelayTransport(localUhid: "B", link: m.link("B"))
        return (a, r, b)
    }

    // ── tests ────────────────────────────────────────────────────────────────

    func test_message_traverses_relay_no_direct_link() {
        let (a, r, b) = buildLine()
        XCTAssertFalse(a.isConnected("B"))

        var got: (String, String)?
        let exp = expectation(description: "B receives")
        b.setOnData { s, d in got = (s, String(decoding: d, as: UTF8.self)); exp.fulfill() }

        XCTAssertTrue(b.reserve("R"))
        a.setRoute("B", relay: "R")
        XCTAssertTrue(a.send("B", Data("deadbeef".utf8)))

        wait(for: [exp], timeout: 3)
        XCTAssertEqual(got?.0, "A")
        XCTAssertEqual(got?.1, "deadbeef")
        XCTAssertEqual(r.activeBridgeCount, 1)
    }

    func test_bridge_is_bidirectional() {
        let (a, _, b) = buildLine()
        let bExp = expectation(description: "B receives")
        b.setOnData { _, _ in bExp.fulfill() }
        var aGot: (String, String)?
        let aExp = expectation(description: "A receives reply")
        a.setOnData { s, d in aGot = (s, String(decoding: d, as: UTF8.self)); aExp.fulfill() }

        XCTAssertTrue(b.reserve("R"))
        a.setRoute("B", relay: "R")
        XCTAssertTrue(a.send("B", Data("hi".utf8)))
        wait(for: [bExp], timeout: 3)

        XCTAssertTrue(b.send("A", Data("reply".utf8)))
        wait(for: [aExp], timeout: 3)
        XCTAssertEqual(aGot?.0, "B")
        XCTAssertEqual(aGot?.1, "reply")
    }

    func test_connect_refused_without_reservation() {
        let (a, r, b) = buildLine()
        let noRecv = expectation(description: "B should not receive"); noRecv.isInverted = true
        b.setOnData { _, _ in noRecv.fulfill() }
        a.setRoute("B", relay: "R") // route known, but B never reserved
        XCTAssertFalse(a.send("B", Data("x".utf8)))
        wait(for: [noRecv], timeout: 0.4)
        XCTAssertEqual(r.activeBridgeCount, 0)
    }

    func test_send_fails_without_route() {
        let (a, _, b) = buildLine()
        XCTAssertTrue(b.reserve("R"))
        XCTAssertFalse(a.send("B", Data("x".utf8))) // no setRoute
    }

    func test_relay_enforces_data_budget() {
        var opts = RelayTransportOptions(); opts.bridgeDataLimitBytes = 10
        let (a, r, b) = buildLine(relayOpts: opts)

        let counter = Counter()
        let first = expectation(description: "first delivered")
        let second = expectation(description: "second must not arrive"); second.isInverted = true
        b.setOnData { _, _ in if counter.inc() == 1 { first.fulfill() } else { second.fulfill() } }

        XCTAssertTrue(b.reserve("R"))
        a.setRoute("B", relay: "R")
        XCTAssertTrue(a.send("B", Data([1, 2, 3, 4, 5]))) // 5 <= 10
        wait(for: [first], timeout: 3)

        a.send("B", Data([6, 7, 8, 9, 10, 11, 12, 13])) // cum 13 > 10 -> dropped + torn down
        wait(for: [second], timeout: 0.5)
        XCTAssertEqual(r.activeBridgeCount, 0)
    }

    func test_reservation_expiry_refuses_connect() {
        let clk = TestClock()
        var opts = RelayTransportOptions(); opts.reservationTTL = 30 * 60
        let (a, _, b) = buildLine(relayOpts: opts, relayClock: clk.now)

        let noRecv = expectation(description: "B should not receive after expiry"); noRecv.isInverted = true
        b.setOnData { _, _ in noRecv.fulfill() }

        XCTAssertTrue(b.reserve("R"))
        a.setRoute("B", relay: "R")
        clk.advance(31 * 60) // past TTL on R's clock
        XCTAssertFalse(a.send("B", Data("x".utf8)))
        wait(for: [noRecv], timeout: 0.4)
    }
}
