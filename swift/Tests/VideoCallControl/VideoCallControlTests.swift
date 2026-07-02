// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Unit tests for ``VideoCallControlService`` (PacketType.videoCall call-control).
///
/// Two halves, mirroring the C# `VideoCallControlTests` case-for-case:
///   1. Byte-identity — the hand-built wire encoder must produce EXACTLY the UTF-8 bytes in
///      `fixtures/videocall/vectors.json` (snake_case, field order call_id/action/sent_at_ms,
///      no whitespace, lowercase-dashed UUID, bare-integer sent_at_ms). This is the cross-language gate.
///   2. Behaviour — directed signalling: a fake ``MeshSender`` captures directed sends;
///      ring mints + sends "ring", accept/decline/hangup echo their verb, handle() raises
///      onCallStateChanged, and a wrong packet type is rejected.
final class VideoCallControlTests: XCTestCase {

    // MARK: - Fakes / capture

    // Directed sends are captured by the shared `FakeMeshSender` (Tests/FakeMeshSender.swift),
    // a `final class` whose `unicasts()` returns the recorded `UnicastRecord`s (`.packet` /
    // `.nextHopUhid`). A per-test nested `actor` fake is deliberately avoided: an `actor`
    // conforming to the nonisolated `MeshSender` requirements trips Swift 6 `#ConformanceIsolation`.

    /// Thread-safe one-shot capture box for the `@Sendable` state-changed callback.
    private final class EventBox: @unchecked Sendable {
        private let lock = NSLock()
        private var value: VideoCallStateChanged?
        func set(_ e: VideoCallStateChanged) { lock.lock(); value = e; lock.unlock() }
        func get() -> VideoCallStateChanged? { lock.lock(); defer { lock.unlock() }; return value }
    }

    // MARK: - Byte-identity corpus

    private struct Vector: Decodable {
        let name: String
        let call_id: String
        let action: String
        let sent_at_ms: Int64
        let expected_json: String
    }

    private struct Corpus: Decodable {
        let description: String
        let vectors: [Vector]
    }

    /// Locate `fixtures/videocall/vectors.json` by walking up from this source file's directory
    /// (`#file`) to the repo root — independent of CWD, the same parent-traversal idiom the URI
    /// and Bandwidth fixture drivers use.
    private func loadCorpus() throws -> Corpus {
        var url = URL(fileURLWithPath: #file).deletingLastPathComponent()
        for _ in 0..<10 {
            let candidate = url
                .appendingPathComponent("fixtures")
                .appendingPathComponent("videocall")
                .appendingPathComponent("vectors.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                let data = try Data(contentsOf: candidate)
                return try JSONDecoder().decode(Corpus.self, from: data)
            }
            let parent = url.deletingLastPathComponent()
            if parent.path == url.path { break }
            url = parent
        }
        XCTFail("Could not locate fixtures/videocall/vectors.json walking up from \(#file)")
        throw CocoaError(.fileNoSuchFile)
    }

    /// Every fixture vector must hand-encode to EXACTLY its `expected_json` bytes.
    func testWire_MatchesCanonicalVectors() throws {
        let corpus = try loadCorpus()
        XCTAssertFalse(corpus.vectors.isEmpty, "corpus has no vectors")

        for v in corpus.vectors {
            let callId = try XCTUnwrap(UUID(uuidString: v.call_id), "[\(v.name)] bad UUID \(v.call_id)")
            let bytes = _videoCallControlWireBytesForTests(callId: callId, action: v.action, sentAtMs: v.sent_at_ms)
            let got = String(decoding: bytes, as: UTF8.self)
            XCTAssertEqual(got, v.expected_json, "[\(v.name)] wire byte mismatch")
        }
    }

    /// The two InlineData cases from the C# test, asserted directly (belt-and-braces alongside
    /// the fixture-driven test, so byte-identity is pinned even if the fixture file moves).
    func testWire_MatchesInlineCanonicalCases() throws {
        let ring = _videoCallControlWireBytesForTests(
            callId: try XCTUnwrap(UUID(uuidString: "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f")),
            action: "ring",
            sentAtMs: 1_700_000_000_000
        )
        XCTAssertEqual(
            String(decoding: ring, as: UTF8.self),
            "{\"call_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"action\":\"ring\",\"sent_at_ms\":1700000000000}"
        )

        let hangup = _videoCallControlWireBytesForTests(
            callId: try XCTUnwrap(UUID(uuidString: "00000000-0000-0000-0000-000000000000")),
            action: "hangup",
            sentAtMs: 0
        )
        XCTAssertEqual(
            String(decoding: hangup, as: UTF8.self),
            "{\"call_id\":\"00000000-0000-0000-0000-000000000000\",\"action\":\"hangup\",\"sent_at_ms\":0}"
        )
    }

    // MARK: - Behaviour

    /// ring() mints a non-empty call id and directed-sends a "ring" VideoCall packet to the peer.
    func testRing_SendsDirectedRingToPeer_AndReturnsCallId() async throws {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = VideoCallControlService(sender: sender)

        let callId = await svc.ring("aether:bob:02")

        let sends = sender.unicasts()
        XCTAssertEqual(sends.count, 1)
        let sent = try XCTUnwrap(sends.first)
        XCTAssertEqual(sent.packet.type, .videoCall)
        XCTAssertEqual(sent.nextHopUhid, "aether:bob:02")
        XCTAssertEqual(sent.packet.destinationUhid, "aether:bob:02")
        XCTAssertEqual(sent.packet.sourceUhid, "aether:alice:01")

        let body = try XCTUnwrap(JSONDecoder().decodeVideoWire(sent.packet.payload))
        XCTAssertEqual(body.action, "ring")
        XCTAssertEqual(body.callId, callId)
    }

    /// accept / decline / hangup each directed-send their verb (echoing the caller-supplied call id).
    func testRespond_SendsDirectedActionToPeer() async throws {
        for action in ["accept", "decline", "hangup"] {
            let sender = FakeMeshSender(localUhid: "aether:local:01")
            let svc = VideoCallControlService(sender: sender)
            let callId = UUID()

            let ok: Bool
            switch action {
            case "accept":  ok = await svc.accept(callId, peerUhid: "aether:bob:02")
            case "decline": ok = await svc.decline(callId, peerUhid: "aether:bob:02")
            default:        ok = await svc.hangup(callId, peerUhid: "aether:bob:02")
            }

            XCTAssertTrue(ok, "[\(action)] send should report success")
            let sends = sender.unicasts()
            XCTAssertEqual(sends.count, 1, "[\(action)] expected exactly one send")
            let sent = try XCTUnwrap(sends.first)
            XCTAssertEqual(sent.nextHopUhid, "aether:bob:02", "[\(action)] next hop")
            XCTAssertEqual(sent.packet.type, .videoCall, "[\(action)] packet type")

            let body = try XCTUnwrap(JSONDecoder().decodeVideoWire(sent.packet.payload))
            XCTAssertEqual(body.action, action, "[\(action)] action")
            XCTAssertEqual(body.callId, callId, "[\(action)] call id")
        }
    }

    /// handle() on a well-formed VideoCall packet fires onCallStateChanged and returns true.
    func testHandle_RaisesCallStateChanged() async throws {
        let svc = VideoCallControlService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let box = EventBox()
        await svc.setOnCallStateChanged { box.set($0) }

        let callId = UUID()
        let ok = await svc.handle(controlPacket(callId: callId, action: "ring", fromUhid: "aether:bob:02"))

        XCTAssertTrue(ok)
        let got = try XCTUnwrap(box.get())
        XCTAssertEqual(got.callId, callId)
        XCTAssertEqual(got.action, "ring")
        XCTAssertEqual(got.fromUhid, "aether:bob:02")
    }

    /// A packet whose type is not .videoCall is rejected (returns false, no event).
    func testHandle_WrongPacketType_ReturnsFalse() async {
        let svc = VideoCallControlService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        var pkt = controlPacket(callId: UUID(), action: "ring", fromUhid: "aether:bob:02")
        pkt.type = .data
        let ok = await svc.handle(pkt)
        XCTAssertFalse(ok)
    }

    /// A .videoCall packet with a malformed payload is rejected (returns false).
    func testHandle_MalformedPayload_ReturnsFalse() async {
        let svc = VideoCallControlService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let pkt = MeshPacket(
            type: .videoCall,
            sourceUhid: "aether:bob:02",
            destinationUhid: "aether:local:01",
            payload: Data("not json".utf8)
        )
        let ok = await svc.handle(pkt)
        XCTAssertFalse(ok)
    }

    // MARK: - Helpers

    /// Build an inbound VideoCall control packet with a canonical (hand-built) payload, mirroring
    /// the C# test's `ControlPacket` helper.
    private func controlPacket(callId: UUID, action: String, fromUhid: String) -> MeshPacket {
        MeshPacket(
            type: .videoCall,
            sourceUhid: fromUhid,
            destinationUhid: "aether:local:01",
            payload: _videoCallControlWireBytesForTests(callId: callId, action: action, sentAtMs: 1)
        )
    }
}

// Decodes the on-wire VideoCall control payload for test assertions (order-independent parse).
private struct _TestVideoWire: Decodable {
    let call_id: UUID
    let action: String
    let sent_at_ms: Int64
}

private extension JSONDecoder {
    func decodeVideoWire(_ data: Data) -> (callId: UUID, action: String, sentAtMs: Int64)? {
        guard let w = try? decode(_TestVideoWire.self, from: data) else { return nil }
        return (w.call_id, w.action, w.sent_at_ms)
    }
}
