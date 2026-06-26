// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

private let LOCAL = "alice"

/// Build a VoiceSignaling packet with a raw JSON payload.
private func voiceSignalingPkt(from: String, to: String, payload: Data) -> MeshPacket {
    var pkt = MeshPacket(type: .voiceSignaling, sourceUhid: from, destinationUhid: to, priority: 32)
    pkt.payload = payload
    return pkt
}

/// Encode a voice offer (no signal_type field).
private func offerPayload(callId: UUID, fromUhid: String, codecs: [String] = ["opus"], sampleRateHz: Int = 48_000) -> Data {
    let codesJSON = codecs.map { "\"\($0)\"" }.joined(separator: ",")
    let s = """
    {"call_id":"\(callId.uuidString.lowercased())","from_uhid":"\(fromUhid)","codecs":[\(codesJSON)],"sample_rate_hz":\(sampleRateHz)}
    """
    return s.data(using: .utf8)!
}

/// Encode a voice control message (accept / hangup).
private func controlPayload(callId: UUID, fromUhid: String, signalType: String) -> Data {
    let s = """
    {"call_id":"\(callId.uuidString.lowercased())","from_uhid":"\(fromUhid)","signal_type":"\(signalType)"}
    """
    return s.data(using: .utf8)!
}

// ── Tests ──────────────────────────────────────────────────────────────────────

final class VoiceCallServiceTests: XCTestCase {

    // MARK: – sendOffer

    func test_sendOffer_returnsNonNilCallId() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", codecs: ["opus"], sampleRateHz: 48_000)
        XCTAssertNotEqual(callId, UUID(uuidString: "00000000-0000-0000-0000-000000000000")!)
    }

    func test_sendOffer_emitsVoiceSignalingPacket() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        _ = try await svc.sendOffer(toUhid: "bob", codecs: ["opus"], sampleRateHz: 48_000)
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .voiceSignaling)
        XCTAssertEqual(unicasts[0].packet.destinationUhid, "bob")
    }

    func test_sendOffer_payloadContainsCodecs() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", codecs: ["opus", "g722"], sampleRateHz: 16_000)
        let pkt = sender.unicasts()[0].packet
        let bodyStr = String(data: pkt.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(bodyStr.contains("opus"), "payload must contain codec name")
        XCTAssertTrue(bodyStr.contains(callId.uuidString.lowercased()), "offer payload must carry a lowercase call_id (cross-language wire parity)")
    }

    // MARK: – inbound offer

    func test_handlePacket_inboundOffer_firesOnIncomingCall() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let firedFrom = Locked("")
        await svc.setOnIncomingCall { _, from, _, _ in firedFrom.value = from }

        let callId = UUID()
        let pkt = voiceSignalingPkt(
            from: "bob", to: LOCAL,
            payload: offerPayload(callId: callId, fromUhid: "bob")
        )
        try await svc.handlePacket(pkt)
        XCTAssertEqual(firedFrom.value, "bob", "onIncomingCall must fire with sender's uhid")
    }

    // MARK: – inbound accept

    func test_handlePacket_inboundAccept_firesOnCallStateChanged_connected() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", codecs: ["opus"], sampleRateHz: 48_000)

        let lastState = Locked<VoiceCallState?>(nil)
        await svc.setOnCallStateChanged { _, s in lastState.value = s }

        let pkt = voiceSignalingPkt(
            from: "bob", to: LOCAL,
            payload: controlPayload(callId: callId, fromUhid: "bob", signalType: "accept")
        )
        try await svc.handlePacket(pkt)
        XCTAssertEqual(lastState.value, .connected)
    }

    // MARK: – inbound hangup

    func test_handlePacket_inboundHangup_firesOnCallStateChanged_ended() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)

        // First receive an offer so there is an active call.
        let callId = UUID()
        let offerPkt = voiceSignalingPkt(
            from: "bob", to: LOCAL,
            payload: offerPayload(callId: callId, fromUhid: "bob")
        )
        try await svc.handlePacket(offerPkt)

        let lastState = Locked<VoiceCallState?>(nil)
        await svc.setOnCallStateChanged { _, s in lastState.value = s }

        let hangupPkt = voiceSignalingPkt(
            from: "bob", to: LOCAL,
            payload: controlPayload(callId: callId, fromUhid: "bob", signalType: "hangup")
        )
        try await svc.handlePacket(hangupPkt)
        XCTAssertEqual(lastState.value, .ended)
    }

    // MARK: – acceptCall

    func test_acceptCall_sendAnswerSignaling() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = UUID()
        let offerPkt = voiceSignalingPkt(
            from: "bob", to: LOCAL,
            payload: offerPayload(callId: callId, fromUhid: "bob")
        )
        try await svc.handlePacket(offerPkt)
        sender.clear()

        try await svc.acceptCall(callId: callId)
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .voiceSignaling)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("accept"), "answer payload must contain signal_type=accept")
    }

    func test_acceptCall_firesOnCallStateChanged_connected() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = UUID()
        let offerPkt = voiceSignalingPkt(
            from: "bob", to: LOCAL,
            payload: offerPayload(callId: callId, fromUhid: "bob")
        )
        try await svc.handlePacket(offerPkt)

        let lastState = Locked<VoiceCallState?>(nil)
        await svc.setOnCallStateChanged { _, s in lastState.value = s }
        try await svc.acceptCall(callId: callId)
        XCTAssertEqual(lastState.value, .connected)
    }

    // MARK: – hangUp

    func test_hangUp_sendsHangupSignaling() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", codecs: ["opus"], sampleRateHz: 48_000)
        sender.clear()

        try await svc.hangUp(callId: callId)
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .voiceSignaling)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("hangup"))
    }

    func test_hangUp_firesOnCallStateChanged_ended() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", codecs: ["opus"], sampleRateHz: 48_000)

        let lastState = Locked<VoiceCallState?>(nil)
        await svc.setOnCallStateChanged { _, s in lastState.value = s }
        try await svc.hangUp(callId: callId)
        XCTAssertEqual(lastState.value, .ended)
    }

    // MARK: – sendFrame

    func test_sendFrame_onConnectedCall_emitsVoiceCallPacket() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", codecs: ["opus"], sampleRateHz: 48_000)

        // Simulate bob accepting.
        let acceptPkt = voiceSignalingPkt(
            from: "bob", to: LOCAL,
            payload: controlPayload(callId: callId, fromUhid: "bob", signalType: "accept")
        )
        try await svc.handlePacket(acceptPkt)
        sender.clear()

        let audio = Data([0xDE, 0xAD, 0xBE, 0xEF])
        try await svc.sendFrame(callId: callId, encodedAudio: audio, isSilence: false)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .voiceCall)
        // Wire: [16 callId][4 seq][8 ts][1 isSilence][N audio]
        XCTAssertGreaterThanOrEqual(unicasts[0].packet.payload.count, 29 + audio.count)
    }

    func test_sendFrame_onNotConnectedCall_doesNotEmitPacket() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", codecs: ["opus"], sampleRateHz: 48_000)
        // Still Outgoing (no answer received).
        sender.clear()
        try await svc.sendFrame(callId: callId, encodedAudio: Data([0x01, 0x02]), isSilence: false)
        XCTAssertTrue(sender.unicasts().isEmpty, "sendFrame on non-connected call must not send anything")
    }

    // MARK: – onFrameReceived

    func test_handlePacket_voiceCallFrame_firesOnFrameReceived() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VoiceCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", codecs: ["opus"], sampleRateHz: 48_000)
        // Connect the call.
        let acceptPkt = voiceSignalingPkt(
            from: "bob", to: LOCAL,
            payload: controlPayload(callId: callId, fromUhid: "bob", signalType: "accept")
        )
        try await svc.handlePacket(acceptPkt)

        let frameReceived = Locked(false)
        await svc.setOnFrameReceived { _, _, _, _ in frameReceived.value = true }

        // Build a binary VoiceCall frame packet.
        var framePkt = MeshPacket(type: .voiceCall, sourceUhid: "bob", destinationUhid: LOCAL, priority: 64)
        var buf = Data(count: 29 + 4)
        // [0..15] call id bytes
        let uuidBytes = withUnsafeBytes(of: callId.uuid) { Data($0) }
        buf.replaceSubrange(0..<16, with: uuidBytes)
        // [16..19] seq = 0 (already zero), [20..27] ts = 0, [28] isSilence = 0
        // [29..32] audio bytes
        buf[29] = 0xAA; buf[30] = 0xBB; buf[31] = 0xCC; buf[32] = 0xDD
        framePkt.payload = buf
        try await svc.handlePacket(framePkt)

        XCTAssertTrue(frameReceived.value, "onFrameReceived must fire for inbound VoiceCall packet")
    }
}
