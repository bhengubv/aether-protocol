// SPDX-License-Identifier: MIT
import XCTest
import Foundation
@testable import AetherNetProtocol

private let LOCAL = "aether:local:01"

/// Mirror used only to DECODE captured Heartbeat payloads in assertions. The real wire struct
/// (`HeartbeatWire`) is `private` to the service; byte-identity of the real encoder is verified
/// separately via `_heartbeatWireBytesForTests`.
private struct HeartbeatWireMirror: Codable {
    let sequence: Int32
    let sent_at_ms: Int64
}

/// Build an inbound Heartbeat packet from `source` (mirrors `HeartbeatFrom` in HeartbeatTests.cs).
/// Encodes the payload via the same snake_case-declaration-order `Codable` path the service uses.
private func heartbeatFrom(source: String, sequence: Int32, sentAtMs: Int64) -> MeshPacket {
    let wire = HeartbeatWireMirror(sequence: sequence, sent_at_ms: sentAtMs)
    let payload = (try? JSONEncoder().encode(wire)) ?? Data()
    return MeshPacket(
        type: .heartbeat,
        sourceUhid: source,
        destinationUhid: "*",
        payload: payload
    )
}

/// Unit tests for ``HeartbeatService`` (PacketType 10). Mirrors
/// `tests/AetherNet.Core.Tests/HeartbeatTests.cs`. Uses the shared in-memory ``FakeMeshSender`` —
/// no transport needed.
final class HeartbeatServiceTests: XCTestCase {

    // MARK: - Byte-identity vectors (fixtures/heartbeat/vectors.json)

    /// "basic" vector — mirrors the [InlineData(1, 1700000000000L, ...)] C# theory row.
    func test_heartbeatWire_basicVector_serializesExactBytes() {
        let data = _heartbeatWireBytesForTests(sequence: 1, sentAtMs: 1_700_000_000_000)
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(json, "{\"sequence\":1,\"sent_at_ms\":1700000000000}")
    }

    /// "zero" vector — mirrors the [InlineData(0, 0L, ...)] C# theory row.
    func test_heartbeatWire_zeroVector_serializesExactBytes() {
        let data = _heartbeatWireBytesForTests(sequence: 0, sentAtMs: 0)
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(json, "{\"sequence\":0,\"sent_at_ms\":0}")
    }

    // MARK: - Send

    func test_send_broadcastsHeartbeat_withIncrementingSequence() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = HeartbeatService(sender: sender)

        await svc.sendHeartbeat()
        await svc.sendHeartbeat()

        let bcasts = sender.broadcasts()
        XCTAssertEqual(bcasts.count, 2)
        for p in bcasts {
            XCTAssertEqual(p.type, .heartbeat)
            XCTAssertEqual(p.ttl, 1)
            XCTAssertEqual(p.destinationUhid, "*")
            XCTAssertEqual(p.sourceUhid, LOCAL)
        }

        let first = try? JSONDecoder().decode(HeartbeatWireMirror.self, from: bcasts[0].payload)
        let second = try? JSONDecoder().decode(HeartbeatWireMirror.self, from: bcasts[1].payload)
        XCTAssertEqual(first?.sequence, 1)
        XCTAssertEqual(second?.sequence, 2)
    }

    /// Delivered count is whatever the sender's fan-out reports (mirrors the C# `Task<int>` contract).
    func test_send_returnsDeliveredCount() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        sender.addPeer(PeerInfo(uhid: "aether:peer:aa"))
        sender.addPeer(PeerInfo(uhid: "aether:peer:bb"))
        let svc = HeartbeatService(sender: sender)

        let delivered = await svc.sendHeartbeat()
        XCTAssertEqual(delivered, 2)
    }

    // MARK: - Handle

    func test_handle_recordsPeerAndFiresEvent() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = HeartbeatService(sender: sender)
        let seen = Locked<PeerLiveness?>(nil)
        await svc.setOnPeerSeen { liveness in seen.value = liveness }

        let ok = await svc.handle(heartbeatFrom(source: "aether:peer:aa", sequence: 7, sentAtMs: 1_700_000_000_000))

        XCTAssertTrue(ok)
        XCTAssertNotNil(seen.value)
        XCTAssertEqual(seen.value?.uhid, "aether:peer:aa")
        XCTAssertEqual(seen.value?.lastSequence, 7)
        XCTAssertEqual(seen.value?.lastSentAtMs, 1_700_000_000_000)

        let known = await svc.getKnownPeers()
        XCTAssertEqual(known.count, 1)
        XCTAssertEqual(known[0].uhid, "aether:peer:aa")
    }

    func test_handle_refreshesExistingPeer() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = HeartbeatService(sender: sender)

        _ = await svc.handle(heartbeatFrom(source: "aether:peer:aa", sequence: 1, sentAtMs: 1000))
        _ = await svc.handle(heartbeatFrom(source: "aether:peer:aa", sequence: 2, sentAtMs: 2000))

        let known = await svc.getKnownPeers()
        XCTAssertEqual(known.count, 1)
        XCTAssertEqual(known[0].lastSequence, 2)
    }

    func test_handle_ownHeartbeat_isIgnored() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = HeartbeatService(sender: sender)

        let ok = await svc.handle(heartbeatFrom(source: LOCAL, sequence: 1, sentAtMs: 1000))
        XCTAssertFalse(ok)
        let known = await svc.getKnownPeers()
        XCTAssertTrue(known.isEmpty)
    }

    func test_handle_wrongPacketType_returnsFalse() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = HeartbeatService(sender: sender)

        var pkt = heartbeatFrom(source: "aether:peer:aa", sequence: 1, sentAtMs: 1000)
        pkt.type = .data
        let ok = await svc.handle(pkt)
        XCTAssertFalse(ok)
        let known = await svc.getKnownPeers()
        XCTAssertTrue(known.isEmpty)
    }

    func test_handle_malformedPayload_returnsFalse() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = HeartbeatService(sender: sender)

        var pkt = heartbeatFrom(source: "aether:peer:aa", sequence: 1, sentAtMs: 1000)
        pkt.payload = Data("not json".utf8)
        let ok = await svc.handle(pkt)
        XCTAssertFalse(ok)
        let known = await svc.getKnownPeers()
        XCTAssertTrue(known.isEmpty)
    }

    // MARK: - GetLivePeers

    func test_getLivePeers_includesRecentlySeenPeer() async {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = HeartbeatService(sender: sender)
        _ = await svc.handle(heartbeatFrom(source: "aether:peer:aa", sequence: 1, sentAtMs: 1000))

        // A just-received heartbeat is live within any generous window.
        let live = await svc.getLivePeers(withinSeconds: 3600)
        XCTAssertEqual(live.count, 1)
        XCTAssertEqual(live[0].uhid, "aether:peer:aa")

        // A negative window pushes the recency horizon into the future, so it excludes even a
        // just-seen peer — a deterministic proof the filter filters (no wall-clock race).
        let none = await svc.getLivePeers(withinSeconds: -1)
        XCTAssertTrue(none.isEmpty)
    }
}
