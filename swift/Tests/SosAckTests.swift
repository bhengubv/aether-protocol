// SPDX-License-Identifier: MIT
import XCTest
import Foundation
@testable import AetherNetProtocol

private let LOCAL = "local"

/// Mirror used only to DECODE captured SosAck payloads in assertions. The real wire struct
/// (`SosAckWire`) is `private` to the service; byte-identity of the real encoder is verified
/// separately via `_sosAckWireBytesForTests`.
private struct SosAckWireMirror: Codable {
    let broadcast_id: String
    let received_at_ms: Int64
}

/// Build an inbound SOS broadcast packet from `source`.
private func inboundSos(source: String, broadcastId: UUID = UUID(), ttl: Int32 = ProtocolConstants.sosTtl) -> MeshPacket {
    let json = "{\"broadcast_id\":\"\(broadcastId.uuidString.lowercased())\",\"broadcast_type\":\"sos\",\"message\":\"help\",\"latitude\":-33.9,\"longitude\":18.4,\"geohash\":null}"
    var pkt = MeshPacket(
        type: .sosBroadcast,
        sourceUhid: source,
        destinationUhid: "",
        ttl: ttl,
        priority: ProtocolConstants.sosPriority
    )
    pkt.payload = json.data(using: .utf8)!
    return pkt
}

/// Build a directed SosAck packet from `responder` targeting `originator` for `broadcastId`.
private func inboundAck(responder: String, originator: String, broadcastId: UUID, receivedAtMs: Int64 = 1_700_000_000_000) -> MeshPacket {
    let json = "{\"broadcast_id\":\"\(broadcastId.uuidString.lowercased())\",\"received_at_ms\":\(receivedAtMs)}"
    var pkt = MeshPacket(
        type: .sosAck,
        sourceUhid: responder,
        destinationUhid: originator,
        ttl: ProtocolConstants.sosTtl,
        priority: ProtocolConstants.sosPriority
    )
    pkt.payload = json.data(using: .utf8)!
    return pkt
}

final class SosAckTests: XCTestCase {

    // MARK: - Byte-identity vectors (fixtures/sos/vectors.json)

    func test_sosAckWire_basicVector_serializesExactBytes() {
        let id = UUID(uuidString: "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f")!
        let data = _sosAckWireBytesForTests(broadcastId: id, receivedAtMs: 1_700_000_000_000)
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(json, "{\"broadcast_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"received_at_ms\":1700000000000}")
    }

    func test_sosAckWire_zeroVector_serializesExactBytes() {
        let id = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!
        let data = _sosAckWireBytesForTests(broadcastId: id, receivedAtMs: 0)
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(json, "{\"broadcast_id\":\"00000000-0000-0000-0000-000000000000\",\"received_at_ms\":0}")
    }

    // MARK: - handle() emits a directed SosAck

    func test_handle_foreignSos_sendsExactlyOneDirectedAckToOriginator() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        let bid = UUID()
        await svc.handle(inboundSos(source: "alice", broadcastId: bid))

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1, "exactly one directed SosAck expected")
        XCTAssertEqual(unicasts[0].nextHopUhid, "alice")
        XCTAssertEqual(unicasts[0].packet.type, .sosAck)
        XCTAssertEqual(unicasts[0].packet.sourceUhid, LOCAL)
        XCTAssertEqual(unicasts[0].packet.destinationUhid, "alice")
        XCTAssertEqual(unicasts[0].packet.ttl, ProtocolConstants.sosTtl)
        XCTAssertEqual(unicasts[0].packet.priority, ProtocolConstants.sosPriority)

        let decoded = try? JSONDecoder().decode(SosAckWireMirror.self, from: unicasts[0].packet.payload)
        XCTAssertEqual(decoded?.broadcast_id, bid.uuidString.lowercased())

        let ackBody = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertEqual(ackBody, ackBody.lowercased(), "SosAck payload must be all-lowercase (cross-language wire parity)")
    }

    func test_handle_ownSos_sendsNoDirectedAck() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        await svc.handle(inboundSos(source: LOCAL))
        XCTAssertEqual(sender.unicasts().count, 0)
    }

    // MARK: - handleAck() on the originator

    func test_handleAck_recordsResponderAndFiresEvent() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        let events = Locked<[SosAcknowledgement]>([])
        await svc.setOnSosAcknowledged { ack in events.value = events.value + [ack] }

        let ok = await svc.broadcast(broadcastType: "sos", message: "help", latitude: 0, longitude: 0)
        XCTAssertTrue(ok)
        let bid = await svc.getActiveAlerts()[0].id

        try await svc.handleAck(inboundAck(responder: "bob", originator: LOCAL, broadcastId: bid))

        let captured = events.value
        XCTAssertEqual(captured.count, 1)
        XCTAssertEqual(captured[0].broadcastId, bid)
        XCTAssertEqual(captured[0].responderUhid, "bob")
        XCTAssertEqual(captured[0].totalAcknowledgements, 1)

        let alert = await svc.getActiveAlerts().first { $0.id == bid }
        XCTAssertEqual(alert?.acknowledgedBy, ["bob"])
    }

    func test_handleAck_duplicateResponderCountedOnce() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        let events = Locked<[SosAcknowledgement]>([])
        await svc.setOnSosAcknowledged { ack in events.value = events.value + [ack] }

        _ = await svc.broadcast(broadcastType: "sos", message: "h", latitude: 0, longitude: 0)
        let bid = await svc.getActiveAlerts()[0].id

        try await svc.handleAck(inboundAck(responder: "bob", originator: LOCAL, broadcastId: bid))
        try await svc.handleAck(inboundAck(responder: "bob", originator: LOCAL, broadcastId: bid, receivedAtMs: 1_700_000_000_999))

        XCTAssertEqual(events.value.count, 1, "same responder must fire the event only once")
        let alert = await svc.getActiveAlerts().first { $0.id == bid }
        XCTAssertEqual(alert?.acknowledgedBy.count, 1)
    }

    func test_handleAck_twoDistinctResponders_countsBoth() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        let events = Locked<[SosAcknowledgement]>([])
        await svc.setOnSosAcknowledged { ack in events.value = events.value + [ack] }

        _ = await svc.broadcast(broadcastType: "sos", message: "h", latitude: 0, longitude: 0)
        let bid = await svc.getActiveAlerts()[0].id

        try await svc.handleAck(inboundAck(responder: "bob", originator: LOCAL, broadcastId: bid))
        try await svc.handleAck(inboundAck(responder: "carol", originator: LOCAL, broadcastId: bid))

        let captured = events.value
        XCTAssertEqual(captured.count, 2)
        XCTAssertEqual(captured.last?.totalAcknowledgements, 2)
        let alert = await svc.getActiveAlerts().first { $0.id == bid }
        XCTAssertEqual(alert?.acknowledgedBy, ["bob", "carol"])
    }

    func test_handleAck_unknownBroadcast_isNoOp() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        let events = Locked<[SosAcknowledgement]>([])
        await svc.setOnSosAcknowledged { ack in events.value = events.value + [ack] }

        // No active alert on this node: it never originated the referenced SOS.
        try await svc.handleAck(inboundAck(responder: "bob", originator: LOCAL, broadcastId: UUID()))
        XCTAssertEqual(events.value.count, 0)
    }

    func test_handleAck_wrongPacketType_throws() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = SosBroadcastService(sender: sender)
        let notAnAck = MeshPacket(type: .sosBroadcast, sourceUhid: "bob", destinationUhid: LOCAL)
        do {
            try await svc.handleAck(notAnAck)
            XCTFail("expected handleAck to throw on a non-SosAck packet")
        } catch {
            // expected
        }
    }
}
