// SPDX-License-Identifier: MIT
// NOTE: CI on Linux is the verification gate.

import Foundation

// ─── VideoCallService ─────────────────────────────────────

/// 1-to-1 mesh video call service.
///
/// Wire format for VideoFrame payload (MeshPacket.payload):
///   [16] CallId  (UUID, RFC4122 big-endian)
///   [4]  Sequence (UInt32 little-endian)
///   [8]  TimestampMs (Int64 little-endian)
///   [1]  IsKeyframe (UInt8: 0 or 1)
///   [N]  EncodedPayload
///
/// Signalling uses JSON (Codable, snake_case) in MeshPacket.payload.
/// Priorities: 64 for video frames, 32 for signalling.
public actor VideoCallService {
    private let sender: any MeshSender

    private var calls: [UUID: VideoCallRecord] = [:]
    private var frameSequence: [UUID: UInt32] = [:]

    /// (callId, fromUhid, videoCodecs, audioCodecs)
    public var onIncomingCall: (@Sendable (UUID, String, [String], [String]) -> Void)?
    public var onCallStateChanged: (@Sendable (UUID, VoiceCallState) -> Void)?
    /// (callId, encodedVideo, isKeyframe, timestampMs)
    public var onFrameReceived: (@Sendable (UUID, Data, Bool, Int64) -> Void)?
    public var onKeyframeRequested: (@Sendable (UUID) -> Void)?
    public var onQualityChanged: (@Sendable (UUID, String) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    // MARK: – Callbacks

    public func setOnIncomingCall(_ cb: (@Sendable (UUID, String, [String], [String]) -> Void)?) {
        onIncomingCall = cb
    }
    public func setOnCallStateChanged(_ cb: (@Sendable (UUID, VoiceCallState) -> Void)?) {
        onCallStateChanged = cb
    }
    public func setOnFrameReceived(_ cb: (@Sendable (UUID, Data, Bool, Int64) -> Void)?) {
        onFrameReceived = cb
    }
    public func setOnKeyframeRequested(_ cb: (@Sendable (UUID) -> Void)?) {
        onKeyframeRequested = cb
    }
    public func setOnQualityChanged(_ cb: (@Sendable (UUID, String) -> Void)?) {
        onQualityChanged = cb
    }

    // MARK: – Originator

    /// Offer a video call to `toUhid`. Returns the call ID.
    public func sendOffer(toUhid: String, videoCodecs: [String], audioCodecs: [String]) async throws -> UUID {
        let callId = UUID()
        calls[callId] = VideoCallRecord(callId: callId, remoteUhid: toUhid, state: .outgoing)
        frameSequence[callId] = 0

        let wire = VideoOfferWire(call_id: callId, from_uhid: sender.localUhid, video_codecs: videoCodecs, audio_codecs: audioCodecs)
        await sendSignaling(encodeJSON(wire), toUhid: toUhid)
        return callId
    }

    // MARK: – Callee

    /// Accept an incoming video call.
    public func acceptCall(callId: UUID) async throws {
        guard var record = calls[callId], record.state == .incoming else { return }
        record.state = .connected
        calls[callId] = record
        frameSequence[callId] = 0

        let wire = VideoControlWire(call_id: callId, from_uhid: sender.localUhid, signal_type: "video_accept")
        await sendSignaling(encodeJSON(wire), toUhid: record.remoteUhid)
        onCallStateChanged?(callId, .connected)
    }

    /// Hang up a video call.
    public func hangUp(callId: UUID) async throws {
        guard let record = calls[callId] else { return }
        let wire = VideoControlWire(call_id: callId, from_uhid: sender.localUhid, signal_type: "video_hangup")
        await sendSignaling(encodeJSON(wire), toUhid: record.remoteUhid)
        calls.removeValue(forKey: callId)
        frameSequence.removeValue(forKey: callId)
        onCallStateChanged?(callId, .ended)
    }

    // MARK: – Frame sending

    /// Send an encoded video frame.
    public func sendFrame(callId: UUID, encodedVideo: Data, isKeyframe: Bool) async throws {
        guard let record = calls[callId], record.state == .connected else { return }
        let seq = frameSequence[callId, default: 0]
        frameSequence[callId] = seq &+ 1
        let tsMs = Int64(Date().timeIntervalSince1970 * 1000)

        var pkt = MeshPacket(type: .videoFrame, sourceUhid: sender.localUhid, destinationUhid: record.remoteUhid, priority: 64)
        pkt.payload = encodeVideoFrame(callId: callId, sequence: seq, timestampMs: tsMs, isKeyframe: isKeyframe, video: encodedVideo)
        _ = await sender.send(pkt, nextHopUhid: record.remoteUhid)
    }

    // MARK: – Control signals

    /// Request a keyframe from the remote peer (PLI / FIR equivalent).
    public func requestKeyframe(callId: UUID) async throws {
        guard let record = calls[callId] else { return }
        let wire = VideoControlWire(call_id: callId, from_uhid: sender.localUhid, signal_type: "keyframe_request")
        await sendSignaling(encodeJSON(wire), toUhid: record.remoteUhid)
    }

    /// Notify the remote peer of a quality preference change (e.g. "480p", "720p").
    public func notifyQualityChange(callId: UUID, quality: String) async throws {
        guard let record = calls[callId] else { return }
        let wire = VideoQualityChangeWire(call_id: callId, from_uhid: sender.localUhid, quality: quality)
        await sendSignaling(encodeJSON(wire), toUhid: record.remoteUhid)
    }

    // MARK: – Inbound dispatch

    public func handlePacket(_ packet: MeshPacket) async throws {
        switch packet.type {
        case .videoSignaling: await handleSignaling(packet)
        case .videoFrame:     handleFrame(packet)
        default: break
        }
    }

    // MARK: – Private

    private func handleSignaling(_ packet: MeshPacket) async {
        if let offer = decodeJSON(VideoOfferWire.self, from: packet.payload) {
            calls[offer.call_id] = VideoCallRecord(callId: offer.call_id, remoteUhid: packet.sourceUhid, state: .incoming)
            onIncomingCall?(offer.call_id, packet.sourceUhid, offer.video_codecs, offer.audio_codecs)
            return
        }
        if let ctrl = decodeJSON(VideoControlWire.self, from: packet.payload) {
            switch ctrl.signal_type {
            case "video_accept":
                if var r = calls[ctrl.call_id] {
                    r.state = .connected
                    calls[ctrl.call_id] = r
                    onCallStateChanged?(ctrl.call_id, .connected)
                }
            case "video_hangup":
                calls.removeValue(forKey: ctrl.call_id)
                frameSequence.removeValue(forKey: ctrl.call_id)
                onCallStateChanged?(ctrl.call_id, .ended)
            case "keyframe_request":
                onKeyframeRequested?(ctrl.call_id)
            default: break
            }
            return
        }
        if let qc = decodeJSON(VideoQualityChangeWire.self, from: packet.payload) {
            onQualityChanged?(qc.call_id, qc.quality)
        }
    }

    private func handleFrame(_ packet: MeshPacket) {
        guard let (callId, _, tsMs, isKeyframe, video) = decodeVideoFrame(packet.payload) else { return }
        onFrameReceived?(callId, video, isKeyframe, tsMs)
    }

    private func sendSignaling(_ payload: Data, toUhid: String) async {
        var pkt = MeshPacket(type: .videoSignaling, sourceUhid: sender.localUhid, destinationUhid: toUhid, priority: 32)
        pkt.payload = payload
        _ = await sender.send(pkt, nextHopUhid: toUhid)
    }
}

// ─── Internal model ───────────────────────────────────────

private struct VideoCallRecord: Sendable {
    var callId: UUID
    var remoteUhid: String
    var state: VoiceCallState   // reuse VoiceCallState enum
}

// ─── JSON wire types ──────────────────────────────────────

private struct VideoOfferWire: Codable {
    let call_id: UUID
    let from_uhid: String
    let video_codecs: [String]
    let audio_codecs: [String]
    // No signal_type — absence disambiguates from control messages during decode
}

private struct VideoControlWire: Codable {
    let call_id: UUID
    let from_uhid: String
    let signal_type: String   // "video_accept" | "video_hangup" | "keyframe_request"
}

private struct VideoQualityChangeWire: Codable {
    let call_id: UUID
    let from_uhid: String
    let quality: String
    let signal_type: String = "quality_change"
    private enum CodingKeys: String, CodingKey {
        case call_id, from_uhid, quality, signal_type
    }
}

// ─── Binary VideoFrame helpers ────────────────────────────
// Layout: [16 CallId BE][4 Seq LE][8 TsMs LE][1 IsKeyframe][N Video]

private func encodeVideoFrame(callId: UUID, sequence: UInt32, timestampMs: Int64, isKeyframe: Bool, video: Data) -> Data {
    var buf = Data(capacity: 29 + video.count)
    var uuidBytes = callId.uuid
    withUnsafeBytes(of: &uuidBytes) { buf.append(contentsOf: $0) }
    var seq = sequence.littleEndian
    withUnsafeBytes(of: &seq) { buf.append(contentsOf: $0) }
    var ts = timestampMs.littleEndian
    withUnsafeBytes(of: &ts) { buf.append(contentsOf: $0) }
    buf.append(isKeyframe ? 1 : 0)
    buf.append(video)
    return buf
}

private func decodeVideoFrame(_ data: Data) -> (UUID, UInt32, Int64, Bool, Data)? {
    guard data.count >= 29 else { return nil }
    let callId    = UUID(uuid: data.subdata(in: 0..<16).withUnsafeBytes { $0.load(as: uuid_t.self) })
    let seq       = UInt32(littleEndian: data.subdata(in: 16..<20).withUnsafeBytes { $0.load(as: UInt32.self) })
    let tsMs      = Int64(littleEndian: data.subdata(in: 20..<28).withUnsafeBytes { $0.load(as: Int64.self) })
    let isKeyframe = data[28] != 0
    return (callId, seq, tsMs, isKeyframe, data.subdata(in: 29..<data.count))
}

private func encodeJSON<T: Encodable>(_ value: T) -> Data {
    (try? JSONEncoder().encode(value)) ?? Data()
}

private func decodeJSON<T: Decodable>(_ type: T.Type, from data: Data) -> T? {
    try? JSONDecoder().decode(type, from: data)
}
