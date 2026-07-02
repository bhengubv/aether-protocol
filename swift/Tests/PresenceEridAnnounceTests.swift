// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Unit tests for PresenceBeacon(21)/PresenceQuery(22) (``PresenceService``) and the
/// EridAnnounce(56) mesh binding (``EridAnnounceService``), mirroring the C#
/// `PresenceEridAnnounceTests` case-for-case.
///
/// Three concerns:
///   1. Presence byte-identity — the hand-built beacon/query wire encoders must produce EXACTLY the
///      UTF-8 bytes in `fixtures/presence/vectors.json` (beacon: snake_case, field order
///      erid/geohash/capabilities/status/sent_at_ms, no whitespace, geohash may be "", bare-integer
///      capabilities/status/sent_at_ms; query: field order query_id/geohash, lowercase-dashed UUID).
///   2. Presence behaviour — a fake ``MeshSender`` captures broadcasts; broadcastBeacon emits a
///      beacon packet, query mints + emits a query packet, handle() raises the matching event,
///      wrong type is rejected, and an empty-erid beacon is rejected.
///   3. EridAnnounce transport — directed send of an opaque encrypted blob (PacketType 56), handle()
///      raises the event, wrong type / empty body rejected; plus a re-pin of the shared ERID
///      announcement frame byte-identity against the existing ``EridAnnouncementCodec`` vector.
///
/// Directed/broadcast sends are captured by the shared `FakeMeshSender` (Tests/FakeMeshSender.swift),
/// a `final class`. A per-test nested `actor` fake is deliberately avoided: an `actor` conforming to
/// the nonisolated `MeshSender` requirements trips Swift 6 `#ConformanceIsolation`.
final class PresenceEridAnnounceTests: XCTestCase {

    // MARK: - Byte-identity corpus (fixtures/presence/vectors.json)

    private struct BeaconVector: Decodable {
        let name: String
        let erid: String
        let geohash: String
        let capabilities: Int
        let status: Int
        let sent_at_ms: Int64
        let expected_json: String
    }

    private struct QueryVector: Decodable {
        let name: String
        let query_id: String
        let geohash: String
        let expected_json: String
    }

    private struct PresenceCorpus: Decodable {
        let description: String
        let beacon_vectors: [BeaconVector]
        let query_vectors: [QueryVector]
    }

    /// Locate `fixtures/presence/vectors.json` by walking up from this source file's directory
    /// (`#file`) to the repo root — independent of CWD, the same parent-traversal idiom the URI,
    /// Bandwidth and VideoCallControl fixture drivers use.
    private func loadPresenceCorpus() throws -> PresenceCorpus {
        var url = URL(fileURLWithPath: #file).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = url
                .appendingPathComponent("fixtures")
                .appendingPathComponent("presence")
                .appendingPathComponent("vectors.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                let data = try Data(contentsOf: candidate)
                return try JSONDecoder().decode(PresenceCorpus.self, from: data)
            }
            let parent = url.deletingLastPathComponent()
            if parent.path == url.path { break }
            url = parent
        }
        XCTFail("Could not locate fixtures/presence/vectors.json walking up from \(#file)")
        throw CocoaError(.fileNoSuchFile)
    }

    /// Every beacon fixture vector must hand-encode to EXACTLY its `expected_json` bytes.
    func testBeaconWire_MatchesCanonicalVectors() throws {
        let corpus = try loadPresenceCorpus()
        XCTAssertFalse(corpus.beacon_vectors.isEmpty, "corpus has no beacon vectors")

        for v in corpus.beacon_vectors {
            let bytes = _presenceBeaconWireBytesForTests(
                erid: v.erid,
                geohash: v.geohash,
                capabilities: v.capabilities,
                status: v.status,
                sentAtMs: v.sent_at_ms
            )
            let got = String(decoding: bytes, as: UTF8.self)
            XCTAssertEqual(got, v.expected_json, "[\(v.name)] beacon wire byte mismatch")
        }
    }

    /// Every query fixture vector must hand-encode to EXACTLY its `expected_json` bytes.
    func testQueryWire_MatchesCanonicalVectors() throws {
        let corpus = try loadPresenceCorpus()
        XCTAssertFalse(corpus.query_vectors.isEmpty, "corpus has no query vectors")

        for v in corpus.query_vectors {
            let qid = try XCTUnwrap(UUID(uuidString: v.query_id), "[\(v.name)] bad UUID \(v.query_id)")
            let bytes = _presenceQueryWireBytesForTests(queryId: qid, geohash: v.geohash)
            let got = String(decoding: bytes, as: UTF8.self)
            XCTAssertEqual(got, v.expected_json, "[\(v.name)] query wire byte mismatch")
        }
    }

    /// The InlineData cases from the C# test, asserted directly (belt-and-braces alongside the
    /// fixture-driven tests, so byte-identity is pinned even if the fixture file moves).
    func testWire_MatchesInlineCanonicalCases() throws {
        // Available beacon.
        let available = _presenceBeaconWireBytesForTests(
            erid: "3B38HPPFG9JXE37Q", geohash: "u4pru", capabilities: 73, status: 1, sentAtMs: 1_700_000_000_000
        )
        XCTAssertEqual(
            String(decoding: available, as: UTF8.self),
            "{\"erid\":\"3B38HPPFG9JXE37Q\",\"geohash\":\"u4pru\",\"capabilities\":73,\"status\":1,\"sent_at_ms\":1700000000000}"
        )

        // Hidden / offline beacon (empty geohash).
        let hidden = _presenceBeaconWireBytesForTests(
            erid: "0Z5BD0HB1Q7W76MY", geohash: "", capabilities: 0, status: 5, sentAtMs: 0
        )
        XCTAssertEqual(
            String(decoding: hidden, as: UTF8.self),
            "{\"erid\":\"0Z5BD0HB1Q7W76MY\",\"geohash\":\"\",\"capabilities\":0,\"status\":5,\"sent_at_ms\":0}"
        )

        // Scoped query.
        let query = _presenceQueryWireBytesForTests(
            queryId: try XCTUnwrap(UUID(uuidString: "11112222-3333-4444-5555-666677778888")),
            geohash: "u4pru"
        )
        XCTAssertEqual(
            String(decoding: query, as: UTF8.self),
            "{\"query_id\":\"11112222-3333-4444-5555-666677778888\",\"geohash\":\"u4pru\"}"
        )
    }

    // MARK: - Presence behaviour

    /// broadcastBeacon emits one PresenceBeacon broadcast; handle() on it raises onBeaconReceived.
    func testBroadcastBeacon_EmitsBeaconPacket_AndHandleRaisesEvent() async throws {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = PresenceService(sender: sender)

        let reached = await svc.broadcastBeacon(
            erid: "3B38HPPFG9JXE37Q", geohash: "u4pru", capabilities: 73, status: 1, sentAtMs: 1_700_000_000_000
        )
        XCTAssertEqual(reached, 0) // FakeMeshSender has no seeded peers → fan-out 0

        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        var sent = try XCTUnwrap(broadcasts.first)
        XCTAssertEqual(sent.type, .presenceBeacon)
        XCTAssertEqual(sent.destinationUhid, "*")
        XCTAssertEqual(sent.ttl, ProtocolConstants.defaultTtl)

        let box = Locked<PresenceBeaconReceived?>(nil)
        await svc.setOnBeaconReceived { box.value = $0 }
        sent.sourceUhid = "aether:alice:01"
        let ok = await svc.handle(sent)

        XCTAssertTrue(ok)
        let got = try XCTUnwrap(box.value)
        XCTAssertEqual(got.erid, "3B38HPPFG9JXE37Q")
        XCTAssertEqual(got.geohash, "u4pru")
        XCTAssertEqual(got.capabilities, 73)
        XCTAssertEqual(got.status, 1)
        XCTAssertEqual(got.sentAtMs, 1_700_000_000_000)
        XCTAssertEqual(got.fromUhid, "aether:alice:01")
    }

    /// query() mints a non-empty query id and emits one PresenceQuery broadcast; handle() raises
    /// onQueryReceived with the same id.
    func testQuery_EmitsQueryPacket_AndHandleRaisesEvent() async throws {
        let sender = FakeMeshSender(localUhid: "aether:bob:02")
        let svc = PresenceService(sender: sender)

        let qid = await svc.query("u4pru")
        XCTAssertNotEqual(qid, try XCTUnwrap(UUID(uuidString: "00000000-0000-0000-0000-000000000000")))

        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        let sent = try XCTUnwrap(broadcasts.first)
        XCTAssertEqual(sent.type, .presenceQuery)

        let body = try XCTUnwrap(JSONDecoder().decodeQueryWire(sent.payload))
        XCTAssertEqual(body.queryId, qid)
        XCTAssertEqual(body.geohash, "u4pru")

        let box = Locked<PresenceQueryReceived?>(nil)
        await svc.setOnQueryReceived { box.value = $0 }
        let ok = await svc.handle(sent)

        XCTAssertTrue(ok)
        let got = try XCTUnwrap(box.value)
        XCTAssertEqual(got.queryId, qid)
        XCTAssertEqual(got.geohash, "u4pru")
    }

    /// A packet whose type is neither presence type is rejected (returns false, no event).
    func testPresenceHandle_WrongType_ReturnsFalse() async {
        let svc = PresenceService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let ok = await svc.handle(MeshPacket(type: .data, payload: Data()))
        XCTAssertFalse(ok)
    }

    /// A PresenceBeacon whose erid is empty is rejected (returns false).
    func testPresenceHandle_BeaconWithEmptyErid_ReturnsFalse() async {
        let svc = PresenceService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let pkt = MeshPacket(
            type: .presenceBeacon,
            sourceUhid: "aether:x:01",
            payload: _presenceBeaconWireBytesForTests(
                erid: "", geohash: "", capabilities: 0, status: 0, sentAtMs: 0
            )
        )
        let ok = await svc.handle(pkt)
        XCTAssertFalse(ok)
    }

    // MARK: - EridAnnounce(56) transport

    /// sendAnnounce directed-sends an opaque encrypted blob as an EridAnnounce packet; handle()
    /// raises onAnnounceReceived carrying the same bytes.
    func testEridAnnounce_Send_EmitsDirectedPacket_AndHandleRaisesEvent() async throws {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = EridAnnounceService(sender: sender)
        let enc = Data([1, 2, 3, 4, 5]) // opaque Signal-encrypted announcement

        let sentOk = await svc.sendAnnounce("aether:bob:02", encrypted: enc)
        XCTAssertTrue(sentOk)

        let sends = sender.unicasts()
        XCTAssertEqual(sends.count, 1)
        let sent = try XCTUnwrap(sends.first)
        XCTAssertEqual(sent.packet.type, .eridAnnounce)
        XCTAssertEqual(sent.nextHopUhid, "aether:bob:02")
        XCTAssertEqual(sent.packet.destinationUhid, "aether:bob:02")
        XCTAssertEqual(sent.packet.sourceUhid, "aether:alice:01")

        let box = Locked<EridAnnounceReceived?>(nil)
        await svc.setOnAnnounceReceived { box.value = $0 }
        var inbound = sent.packet
        inbound.sourceUhid = "aether:bob:02"
        let ok = await svc.handle(inbound)

        XCTAssertTrue(ok)
        let got = try XCTUnwrap(box.value)
        XCTAssertEqual(got.encryptedAnnouncement, enc)
        XCTAssertEqual(got.fromUhid, "aether:bob:02")
    }

    /// handle() rejects the wrong packet type and an empty-body EridAnnounce (returns false).
    func testEridAnnounce_Handle_WrongTypeOrEmpty_ReturnsFalse() async {
        let svc = EridAnnounceService(sender: FakeMeshSender(localUhid: "aether:local:01"))

        let wrongType = await svc.handle(MeshPacket(type: .data, payload: Data([1])))
        XCTAssertFalse(wrongType)

        let empty = await svc.handle(MeshPacket(type: .eridAnnounce, payload: Data()))
        XCTAssertFalse(empty)
    }

    /// sendAnnounce rejects an empty peer UHID or an empty announcement (returns false, no send).
    func testEridAnnounce_Send_EmptyArgs_ReturnsFalse() async {
        let sender = FakeMeshSender(localUhid: "aether:local:01")
        let svc = EridAnnounceService(sender: sender)

        let emptyPeer = await svc.sendAnnounce("", encrypted: Data([1, 2, 3]))
        XCTAssertFalse(emptyPeer)

        let emptyBody = await svc.sendAnnounce("aether:bob:02", encrypted: Data())
        XCTAssertFalse(emptyBody)

        XCTAssertEqual(sender.unicasts().count, 0)
    }

    /// Re-pin the shared ERID-announcement frame byte-identity (existing 8/8 codec) against the
    /// fixtures/erid routing key + rotation params. Mirrors the C#
    /// `EridAnnouncementCodec_MatchesCanonicalFrame` case.
    func testEridAnnouncementCodec_MatchesCanonicalFrame() throws {
        let routingKey = try hexToBytes("8f3aa76cdbe9a2b47c5813504023a77bda134c31aa096b51392fb29cdd57ddca")
        let frame = try EridAnnouncementCodec.encode(routingKey, epochSeconds: 900, eridLength: 16)
        XCTAssertEqual(
            hexString(frame),
            "41455244010000038400000010000000208f3aa76cdbe9a2b47c5813504023a77bda134c31aa096b51392fb29cdd57ddca"
        )
    }

    // MARK: - Helpers

    private func hexString(_ bytes: [UInt8]) -> String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }

    private func hexToBytes(_ hex: String) throws -> [UInt8] {
        let chars = Array(hex)
        guard chars.count % 2 == 0 else { throw CocoaError(.coderInvalidValue) }
        var out = [UInt8]()
        out.reserveCapacity(chars.count / 2)
        var i = 0
        while i < chars.count {
            guard let b = UInt8(String(chars[i...(i + 1)]), radix: 16) else { throw CocoaError(.coderInvalidValue) }
            out.append(b)
            i += 2
        }
        return out
    }
}

// Decodes the on-wire PresenceQuery payload for test assertions (order-independent parse).
private struct _TestQueryWire: Decodable {
    let query_id: UUID
    let geohash: String
}

private extension JSONDecoder {
    func decodeQueryWire(_ data: Data) -> (queryId: UUID, geohash: String)? {
        guard let w = try? decode(_TestQueryWire.self, from: data) else { return nil }
        return (w.query_id, w.geohash)
    }
}
