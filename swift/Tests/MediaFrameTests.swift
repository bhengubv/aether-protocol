// SPDX-License-Identifier: MIT
import Foundation
import XCTest
@testable import AetherNetProtocol

/// Unit tests for the VoicePtt(15) + ScreenShare(32) media-frame bindings — the Swift mirror of the
/// C# `MediaFrameTests`. Binary frames sharing the 29-byte header (call_id big-endian,
/// sequence/timestamp little-endian, flag). Byte-identity gates driven by the SHARED
/// `fixtures/media/vectors.json`, plus send/handle behaviour over the shared ``FakeMeshSender``.
final class MediaFrameTests: XCTestCase {

    private static let callId = UUID(uuidString: "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f")!

    private func hex(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    private func hexToData(_ s: String) -> Data {
        var out = Data(capacity: s.count / 2)
        var i = s.startIndex
        while i < s.endIndex {
            let next = s.index(i, offsetBy: 2)
            out.append(UInt8(s[i..<next], radix: 16) ?? 0)
            i = next
        }
        return out
    }

    // ── Shared fixture ───────────────────────────────────────────────────────

    private struct Vectors: Decodable {
        struct VoicePtt: Decodable {
            let name: String
            let call_id: String
            let sequence: UInt32
            let timestamp_ms: Int64
            let is_silence: Bool
            let payload_hex: String
            let expected_hex: String
        }
        struct ScreenShare: Decodable {
            let name: String
            let call_id: String
            let sequence: UInt32
            let timestamp_ms: Int64
            let is_keyframe: Bool
            let payload_hex: String
            let expected_hex: String
        }
        let voice_ptt_vectors: [VoicePtt]
        let screen_share_vectors: [ScreenShare]
    }

    private func loadVectors() throws -> Vectors {
        // .../swift/Tests/MediaFrameTests.swift → walk up to the repo root, then the shared fixture.
        let repoRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
        let url = repoRoot.appendingPathComponent("fixtures/media/vectors.json")
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }

    // ── Byte-identity gates (SHARED fixture) ─────────────────────────────────

    func test_voicePtt_serializesToCanonicalFixtureBytes() throws {
        let vectors = try loadVectors()
        XCTAssertFalse(vectors.voice_ptt_vectors.isEmpty)
        for v in vectors.voice_ptt_vectors {
            let frame = VoicePttFrame(
                callId: UUID(uuidString: v.call_id)!,
                sequence: v.sequence,
                timestampMs: v.timestamp_ms,
                isSilence: v.is_silence,
                encodedPayload: hexToData(v.payload_hex)
            )
            XCTAssertEqual(hex(MediaFrameCodec.serializeVoicePtt(frame)), v.expected_hex,
                           "voice_ptt vector '\(v.name)' must serialize byte-for-byte")
        }
    }

    func test_screenShare_serializesToCanonicalFixtureBytes() throws {
        let vectors = try loadVectors()
        XCTAssertFalse(vectors.screen_share_vectors.isEmpty)
        for v in vectors.screen_share_vectors {
            let frame = ScreenShareFrame(
                callId: UUID(uuidString: v.call_id)!,
                sequence: v.sequence,
                timestampMs: v.timestamp_ms,
                isKeyframe: v.is_keyframe,
                encodedPayload: hexToData(v.payload_hex)
            )
            XCTAssertEqual(hex(MediaFrameCodec.serializeScreenShare(frame)), v.expected_hex,
                           "screen_share vector '\(v.name)' must serialize byte-for-byte")
        }
    }

    // Explicit hard-coded gates (belt-and-braces alongside the fixture-driven loop above).

    func test_voicePtt_frame_hardcodedGate() {
        let f = VoicePttFrame(callId: Self.callId, sequence: 42, timestampMs: 1_700_000_000_000,
                              isSilence: false, encodedPayload: Data([0xAA, 0xBB, 0xCC]))
        XCTAssertEqual(hex(MediaFrameCodec.serializeVoicePtt(f)),
                       "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2a0000000068e5cf8b01000000aabbcc")
    }

    func test_voicePtt_silenceEmpty_hardcodedGate() {
        let f = VoicePttFrame(callId: Self.callId, sequence: 43, timestampMs: 1_700_000_000_020,
                              isSilence: true, encodedPayload: Data())
        XCTAssertEqual(hex(MediaFrameCodec.serializeVoicePtt(f)),
                       "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2b0000001468e5cf8b01000001")
    }

    func test_screenShare_keyframe_hardcodedGate() {
        let f = ScreenShareFrame(callId: Self.callId, sequence: 7, timestampMs: 1_700_000_000_000,
                                 isKeyframe: true, encodedPayload: Data([0x11, 0x22, 0x33, 0x44]))
        XCTAssertEqual(hex(MediaFrameCodec.serializeScreenShare(f)),
                       "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f070000000068e5cf8b0100000111223344")
    }

    func test_screenShare_deltaEmpty_hardcodedGate() {
        let f = ScreenShareFrame(callId: UUID(uuidString: "00000000-0000-0000-0000-000000000000")!,
                                 sequence: 0, timestampMs: 0, isKeyframe: false, encodedPayload: Data())
        XCTAssertEqual(hex(MediaFrameCodec.serializeScreenShare(f)),
                       "0000000000000000000000000000000000000000000000000000000000")
    }

    // ── Round-trips ──────────────────────────────────────────────────────────

    func test_voicePtt_roundTrips() throws {
        let f = VoicePttFrame(callId: Self.callId, sequence: 99, timestampMs: 123_456_789,
                              isSilence: true, encodedPayload: Data([1, 2, 3, 4, 5]))
        let back = try XCTUnwrap(MediaFrameCodec.deserializeVoicePtt(MediaFrameCodec.serializeVoicePtt(f)))
        XCTAssertEqual(back.callId, Self.callId)
        XCTAssertEqual(back.sequence, 99)
        XCTAssertEqual(back.timestampMs, 123_456_789)
        XCTAssertTrue(back.isSilence)
        XCTAssertEqual(back.encodedPayload, Data([1, 2, 3, 4, 5]))
    }

    func test_screenShare_roundTrips_keyframeAndCallIdBigEndian() throws {
        let f = ScreenShareFrame(callId: Self.callId, sequence: 5, timestampMs: 999,
                                 isKeyframe: true, encodedPayload: Data([0xFF]))
        let back = try XCTUnwrap(MediaFrameCodec.deserializeScreenShare(MediaFrameCodec.serializeScreenShare(f)))
        XCTAssertEqual(back.callId, Self.callId)
        XCTAssertTrue(back.isKeyframe)
        XCTAssertEqual(back.encodedPayload, Data([0xFF]))
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    func test_voicePtt_send_emitsDirectedFrame_andHandleFiresCallback() async throws {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = VoicePttService(sender: sender)
        let frame = VoicePttFrame(callId: Self.callId, sequence: 42, timestampMs: 1_700_000_000_000,
                                  encodedPayload: Data([0xAA, 0xBB, 0xCC]))

        let sendOk = await svc.sendFrame(peerUhid: "aether:bob:02", frame: frame)
        XCTAssertTrue(sendOk)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        let sent = unicasts[0]
        XCTAssertEqual(sent.packet.type, .voicePtt)
        XCTAssertEqual(sent.nextHopUhid, "aether:bob:02")
        XCTAssertEqual(sent.packet.destinationUhid, "aether:bob:02")

        let got = Locked<VoicePttFrame?>(nil)
        let gotFrom = Locked("")
        await svc.setOnFrameReceived { f, from in got.value = f; gotFrom.value = from }

        // MeshPacket is a value-type struct: copy out of the `let` record before mutating a field.
        var inbound = sent.packet
        inbound.sourceUhid = "aether:alice:01"
        let handled = await svc.handle(inbound)
        XCTAssertTrue(handled)

        let received = got.value
        XCTAssertNotNil(received)
        XCTAssertEqual(received?.sequence, 42)
        XCTAssertEqual(received?.encodedPayload, Data([0xAA, 0xBB, 0xCC]))
        XCTAssertEqual(gotFrom.value, "aether:alice:01")
    }

    func test_screenShare_send_emitsDirectedFrame_andHandleFiresCallback() async throws {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = ScreenShareService(sender: sender)
        let frame = ScreenShareFrame(callId: Self.callId, sequence: 7, timestampMs: 1_700_000_000_000,
                                     isKeyframe: true, encodedPayload: Data([0x11, 0x22, 0x33, 0x44]))

        let sendOk = await svc.sendFrame(peerUhid: "aether:bob:02", frame: frame)
        XCTAssertTrue(sendOk)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        let sent = unicasts[0]
        XCTAssertEqual(sent.packet.type, .screenShare)
        XCTAssertEqual(sent.nextHopUhid, "aether:bob:02")

        let got = Locked<ScreenShareFrame?>(nil)
        await svc.setOnFrameReceived { f, _ in got.value = f }

        let handled = await svc.handle(sent.packet)
        XCTAssertTrue(handled)

        let received = got.value
        XCTAssertNotNil(received)
        XCTAssertEqual(received?.isKeyframe, true)
        XCTAssertEqual(received?.sequence, 7)
    }

    func test_handle_wrongType_returnsFalse() async {
        let vp = VoicePttService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        let ss = ScreenShareService(sender: FakeMeshSender(localUhid: "aether:local:01"))

        var dataPkt = MeshPacket(type: .data)
        dataPkt.payload = Data(count: 40)

        let vpHandled = await vp.handle(dataPkt)
        XCTAssertFalse(vpHandled)
        let ssHandled = await ss.handle(dataPkt)
        XCTAssertFalse(ssHandled)
    }

    func test_handle_shortFrame_returnsFalse() async {
        let vp = VoicePttService(sender: FakeMeshSender(localUhid: "aether:local:01"))
        var shortPkt = MeshPacket(type: .voicePtt)
        shortPkt.payload = Data(count: 10)   // < 29-byte header
        let handled = await vp.handle(shortPkt)
        XCTAssertFalse(handled)
    }
}
