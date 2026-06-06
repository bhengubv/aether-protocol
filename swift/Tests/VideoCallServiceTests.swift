// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

private let LOCAL = "alice"

/// Build a VideoSignaling packet carrying a JSON payload.
private func videoSignalingPkt(from: String, to: String, payload: Data) -> MeshPacket {
    var pkt = MeshPacket(type: .videoSignaling, sourceUhid: from, destinationUhid: to, priority: 32)
    pkt.payload = payload
    return pkt
}

/// Encode a video offer (no signal_type field — disambiguated by field presence).
private func offerPayload(callId: UUID, fromUhid: String,
                          videoCodecs: [String] = ["h264"],
                          audioCodecs: [String] = ["opus"]) -> Data {
    let vJSON = videoCodecs.map { "\"\($0)\"" }.joined(separator: ",")
    let aJSON = audioCodecs.map { "\"\($0)\"" }.joined(separator: ",")
    let s = """
    {"call_id":"\(callId.uuidString.lowercased())","from_uhid":"\(fromUhid)","video_codecs":[\(vJSON)],"audio_codecs":[\(aJSON)]}
    """
    return s.data(using: .utf8)!
}

/// Encode a video control message (video_accept / video_hangup / keyframe_request).
private func controlPayload(callId: UUID, fromUhid: String, signalType: String) -> Data {
    let s = """
    {"call_id":"\(callId.uuidString.lowercased())","from_uhid":"\(fromUhid)","signal_type":"\(signalType)"}
    """
    return s.data(using: .utf8)!
}

/// Encode a quality-change message.
private func qualityPayload(callId: UUID, fromUhid: String, quality: String) -> Data {
    let s = """
    {"call_id":"\(callId.uuidString.lowercased())","from_uhid":"\(fromUhid)","quality":"\(quality)","signal_type":"quality_change"}
    """
    return s.data(using: .utf8)!
}

/// Build a binary VideoFrame packet.
/// Wire: [16 callId BE][4 seq LE][8 tsMs LE][1 isKeyframe][N video]
private func videoFramePkt(callId: UUID, isKeyframe: Bool = false, video: Data) -> MeshPacket {
    var buf = Data(count: 29 + video.count)
    let uuidBytes = withUnsafeBytes(of: callId.uuid) { Data($0) }
    buf.replaceSubrange(0..<16, with: uuidBytes)
    // seq = 0, tsMs = 0 already zero
    buf[28] = isKeyframe ? 1 : 0
    buf.replaceSubrange(29..<(29 + video.count), with: video)
    var pkt = MeshPacket(type: .videoFrame, sourceUhid: "bob", destinationUhid: LOCAL, priority: 64)
    pkt.payload = buf
    return pkt
}

// ── Tests ──────────────────────────────────────────────────────────────────────

final class VideoCallServiceTests: XCTestCase {

    // MARK: – sendOffer

    func test_sendOffer_returnsNonNilCallId() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])
        XCTAssertNotEqual(callId, UUID(uuidString: "00000000-0000-0000-0000-000000000000")!)
    }

    func test_sendOffer_emitsVideoSignalingPacket() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        _ = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .videoSignaling)
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")
    }

    func test_sendOffer_payloadContainsVideoCodec() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        _ = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["vp9"], audioCodecs: ["opus"])
        let body = String(data: sender.unicasts()[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("vp9"), "offer payload must contain video codec name")
    }

    // MARK: – inbound offer

    func test_handlePacket_inboundOffer_firesOnIncomingCall() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        var firedFrom = ""
        var firedVideoCodecs: [String] = []
        await svc.setOnIncomingCall { _, from, vCodecs, _ in
            firedFrom = from
            firedVideoCodecs = vCodecs
        }
        let callId = UUID()
        let pkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                    payload: offerPayload(callId: callId, fromUhid: "bob", videoCodecs: ["h264"]))
        try await svc.handlePacket(pkt)
        XCTAssertEqual(firedFrom, "bob")
        XCTAssertEqual(firedVideoCodecs, ["h264"])
    }

    // MARK: – inbound accept

    func test_handlePacket_inboundAccept_firesOnCallStateChanged_connected() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])

        var lastState: VoiceCallState?
        await svc.setOnCallStateChanged { _, s in lastState = s }

        let pkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                    payload: controlPayload(callId: callId, fromUhid: "bob", signalType: "video_accept"))
        try await svc.handlePacket(pkt)
        XCTAssertEqual(lastState, .connected)
    }

    // MARK: – inbound hangup

    func test_handlePacket_inboundHangup_firesOnCallStateChanged_ended() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)

        // Receive an offer first so a call record exists.
        let callId = UUID()
        let offerPkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                         payload: offerPayload(callId: callId, fromUhid: "bob"))
        try await svc.handlePacket(offerPkt)

        var lastState: VoiceCallState?
        await svc.setOnCallStateChanged { _, s in lastState = s }

        let hangupPkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                          payload: controlPayload(callId: callId, fromUhid: "bob", signalType: "video_hangup"))
        try await svc.handlePacket(hangupPkt)
        XCTAssertEqual(lastState, .ended)
    }

    // MARK: – acceptCall

    func test_acceptCall_sendsVideoAcceptSignaling() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = UUID()
        let offerPkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                         payload: offerPayload(callId: callId, fromUhid: "bob"))
        try await svc.handlePacket(offerPkt)
        sender.clear()

        try await svc.acceptCall(callId: callId)
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .videoSignaling)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("video_accept"), "accept payload must contain signal_type=video_accept")
    }

    func test_acceptCall_firesOnCallStateChanged_connected() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = UUID()
        let offerPkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                         payload: offerPayload(callId: callId, fromUhid: "bob"))
        try await svc.handlePacket(offerPkt)

        var lastState: VoiceCallState?
        await svc.setOnCallStateChanged { _, s in lastState = s }
        try await svc.acceptCall(callId: callId)
        XCTAssertEqual(lastState, .connected)
    }

    // MARK: – hangUp

    func test_hangUp_sendsHangupSignaling() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])
        sender.clear()

        try await svc.hangUp(callId: callId)
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .videoSignaling)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("video_hangup"), "hangup payload must contain signal_type=video_hangup")
    }

    func test_hangUp_firesOnCallStateChanged_ended() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])

        var lastState: VoiceCallState?
        await svc.setOnCallStateChanged { _, s in lastState = s }
        try await svc.hangUp(callId: callId)
        XCTAssertEqual(lastState, .ended)
    }

    // MARK: – sendFrame

    func test_sendFrame_onConnectedCall_emitsVideoFramePacket() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])

        // Simulate bob accepting.
        let acceptPkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                          payload: controlPayload(callId: callId, fromUhid: "bob", signalType: "video_accept"))
        try await svc.handlePacket(acceptPkt)
        sender.clear()

        let video = Data([0xDE, 0xAD, 0xBE, 0xEF])
        try await svc.sendFrame(callId: callId, encodedVideo: video, isKeyframe: true)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .videoFrame)
        // Wire: [16 callId][4 seq][8 ts][1 isKeyframe][N video]
        XCTAssertGreaterThanOrEqual(unicasts[0].packet.payload.count, 29 + video.count)
        // isKeyframe flag at offset 28 must be 1
        XCTAssertEqual(unicasts[0].packet.payload[28], 1)
    }

    func test_sendFrame_onNotConnectedCall_doesNotEmitPacket() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])
        // Still Outgoing — no accept received.
        sender.clear()
        try await svc.sendFrame(callId: callId, encodedVideo: Data([0x01, 0x02]), isKeyframe: false)
        XCTAssertTrue(sender.unicasts().isEmpty, "sendFrame on non-connected call must not send anything")
    }

    // MARK: – requestKeyframe

    func test_requestKeyframe_sendsKeyframeRequestSignal() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])
        sender.clear()

        try await svc.requestKeyframe(callId: callId)
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("keyframe_request"), "keyframe request payload must contain keyframe_request signal_type")
    }

    func test_handlePacket_keyframeRequest_firesOnKeyframeRequested() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])

        var keyframeRequestedId: UUID?
        await svc.setOnKeyframeRequested { id in keyframeRequestedId = id }

        let pkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                    payload: controlPayload(callId: callId, fromUhid: "bob", signalType: "keyframe_request"))
        try await svc.handlePacket(pkt)
        XCTAssertEqual(keyframeRequestedId, callId)
    }

    // MARK: – notifyQualityChange

    func test_notifyQualityChange_sendsQualityChangeSignal() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])
        sender.clear()

        try await svc.notifyQualityChange(callId: callId, quality: "720p")
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("720p"), "quality change payload must contain the quality string")
        XCTAssertTrue(body.contains("quality_change"), "quality change payload must contain signal_type=quality_change")
    }

    func test_handlePacket_qualityChange_firesOnQualityChanged() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)
        let callId = try await svc.sendOffer(toUhid: "bob", videoCodecs: ["h264"], audioCodecs: ["opus"])

        var changedQuality = ""
        await svc.setOnQualityChanged { _, q in changedQuality = q }

        let pkt = videoSignalingPkt(from: "bob", to: LOCAL,
                                    payload: qualityPayload(callId: callId, fromUhid: "bob", quality: "480p"))
        try await svc.handlePacket(pkt)
        XCTAssertEqual(changedQuality, "480p")
    }

    // MARK: – onFrameReceived

    func test_handlePacket_videoFrame_firesOnFrameReceived() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = VideoCallService(sender: sender)

        var frameReceived = false
        var receivedIsKeyframe = false
        await svc.setOnFrameReceived { _, _, isKf, _ in
            frameReceived = true
            receivedIsKeyframe = isKf
        }

        let callId = UUID()
        let video = Data([0x11, 0x22, 0x33, 0x44])
        let framePkt = videoFramePkt(callId: callId, isKeyframe: true, video: video)
        try await svc.handlePacket(framePkt)

        XCTAssertTrue(frameReceived, "onFrameReceived must fire for inbound VideoFrame packet")
        XCTAssertTrue(receivedIsKeyframe, "isKeyframe flag must be decoded correctly")
    }
}
