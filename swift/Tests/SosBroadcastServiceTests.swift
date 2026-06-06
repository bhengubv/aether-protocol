// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

private let LOCAL = "local"

private struct SosWireMirror: Codable {
    let broadcast_id: UUID
    let broadcast_type: String
    let message: String?
    let latitude: Double
    let longitude: Double
    let geohash: String?
}

private func newSosPacket(source: String, ttl: Int32) -> MeshPacket {
    let wire = SosWireMirror(
        broadcast_id: UUID(),
        broadcast_type: "sos",
        message: "help",
        latitude: -33.9,
        longitude: 18.4,
        geohash: nil
    )
    let payload = (try? JSONEncoder().encode(wire)) ?? Data()
    return MeshPacket(
        type: .sosBroadcast,
        sourceUhid: source,
        destinationUhid: "",
        ttl: ttl,
        priority: ProtocolConstants.sosPriority,
        payload: payload
    )
}

final class SosBroadcastServiceTests: XCTestCase {

    // MARK: - Broadcast

    func test_broadcast_floodsAndStoresAlert() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        let ok = await svc.broadcast(broadcastType: "sos", message: "help", latitude: -33.9, longitude: 18.4)
        XCTAssertTrue(ok)
        let bcasts = sender.broadcasts()
        XCTAssertEqual(bcasts.count, 1)
        XCTAssertEqual(bcasts[0].type, .sosBroadcast)
        XCTAssertEqual(bcasts[0].ttl, ProtocolConstants.sosTtl)
        XCTAssertEqual(bcasts[0].priority, ProtocolConstants.sosPriority)
        let alerts = await svc.getActiveAlerts()
        XCTAssertEqual(alerts.count, 1)
    }

    func test_broadcast_rateLimitedAfterMax() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        for _ in 0..<ProtocolConstants.maxSosBroadcastsPerHour {
            let ok = await svc.broadcast(broadcastType: "sos", message: "h", latitude: 0, longitude: 0)
            XCTAssertTrue(ok)
        }
        let blocked = await svc.broadcast(broadcastType: "sos", message: "h", latitude: 0, longitude: 0)
        XCTAssertFalse(blocked)
    }

    func test_broadcast_rejectsEmptyType() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        let ok = await svc.broadcast(broadcastType: "", message: "help", latitude: 0, longitude: 0)
        XCTAssertFalse(ok)
    }

    // MARK: - Handle

    func test_handle_dropsDuplicatePacketId() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        var pkt = newSosPacket(source: "alice", ttl: ProtocolConstants.sosTtl)
        let pktId = pkt.id

        await svc.handle(pkt)
        sender.clear()
        let alertsAfter = await svc.getActiveAlerts().count

        var pkt2 = newSosPacket(source: "alice", ttl: ProtocolConstants.sosTtl)
        pkt2.id = pktId
        await svc.handle(pkt2)

        XCTAssertEqual(sender.broadcasts().count, 0)
        let alertsNow = await svc.getActiveAlerts().count
        XCTAssertEqual(alertsNow, alertsAfter)

        // pkt is intentionally read but not mutated after first handle — discard
        // to silence the "never mutated" warning without a self-assignment.
        _ = pkt.ttl
    }

    func test_handle_ignoresSelfOriginated() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        await svc.handle(newSosPacket(source: LOCAL, ttl: ProtocolConstants.sosTtl))
        XCTAssertEqual(sender.broadcasts().count, 0)
    }

    func test_handle_rebroadcastsWhenTtlAllows() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        await svc.handle(newSosPacket(source: "alice", ttl: 5))
        let bcasts = sender.broadcasts()
        XCTAssertEqual(bcasts.count, 1)
        XCTAssertEqual(bcasts[0].ttl, 4)
    }

    func test_handle_doesNotRebroadcastWhenTtlExhausted() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        await svc.handle(newSosPacket(source: "alice", ttl: 1))
        XCTAssertEqual(sender.broadcasts().count, 0)
    }

    // MARK: - Resolve

    func test_resolve_removesAlert() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        _ = await svc.broadcast(broadcastType: "sos", message: "h", latitude: 0, longitude: 0)
        let alerts = await svc.getActiveAlerts()
        XCTAssertEqual(alerts.count, 1)
        await svc.resolve(alerts[0].id)
        let after = await svc.getActiveAlerts()
        XCTAssertTrue(after.isEmpty)
    }
}
