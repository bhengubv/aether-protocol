// SPDX-License-Identifier: MIT
// NOTE: Build verification on Windows requires Swift toolchain + VS Build Tools.
// CI on Linux (ubuntu-latest + swift:5.9 container) is the verification gate.

import Foundation

// ─── VoiceCallState ───────────────────────────────────────

/// State machine for a 1-to-1 voice call.
public enum VoiceCallState: Equatable, Sendable {
    case outgoing   // offer sent, waiting for accept
    case incoming   // offer received, waiting for local accept/reject
    case connected  // both sides accepted; frames flow
    case ended      // normal hangup
    case failed     // timeout or transport error
}

// ─── VoiceCallService ─────────────────────────────────────

/// 1-to-1 mesh voice call service.
///
/// Wire format for VoiceFrame payload (MeshPacket.payload):
///   [16] CallId  (UUID, RFC4122 big-endian byte order — uuid tuple order)
///   [4]  Sequence (UInt32 little-endian)
///   [8]  TimestampMs (Int64 little-endian)
///   [1]  IsSilence (UInt8: 0 or 1)
///   [N]  EncodedPayload
///
/// Signalling uses JSON (Codable, snake_case CodingKeys) in MeshPacket.payload.
/// Packet priorities: 64 for audio frames, 32 for signalling.
public actor VoiceCallService {
    private let sender: any MeshSender

    private var calls: [UUID: VoiceCallRecord] = [:]
    private var frameSequence: [UUID: UInt32] = [:]

    public var onIncomingCall: (@Sendable (UUID, String, [String], Int) -> Void)?
    public var onCallStateChanged: (@Sendable (UUID, VoiceCallState) -> Void)?
    public var onFrameReceived: (@Sendable (UUID, Data, Bool, Int64) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    // MARK: – Callbacks

    public func setOnIncomingCall(_ cb: (@Sendable (UUID, String, [String], Int) -> Void)?) {
        onIncomingCall = cb
    }
    public func setOnCallStateChanged(_ cb: (@Sendable (UUID, VoiceCallState) -> Void)?) {
        onCallStateChanged = cb
    }
    public func setOnFrameReceived(_ cb: (@Sendable (UUID, Data, Bool, Int64) -> Void)?) {
        onFrameReceived = cb
    }

    // MARK: – Originator

    /// Send a call offer to `toUhid`. Returns the new call ID.
    public func sendOffer(toUhid: String, codecs: [String], sampleRateHz: Int) async throws -> UUID {
        let callId = UUID()
        calls[callId] = VoiceCallRecord(callId: callId, remoteUhid: toUhid, state: .outgoing)
        frameSequence[callId] = 0

        let wire = VoiceOfferWire(call_id: callId, from_uhid: sender.localUhid, codecs: codecs, sample_rate_hz: sampleRateHz)
        var pkt = MeshPacket(type: .voiceSignaling, sourceUhid: sender.localUhid, destinationUhid: toUhid, priority: 32)
        pkt.payload = encodeJSON(wire)
        _ = await sender.send(pkt, nextHopUhid: toUhid)
        return callId
    }

    // MARK: – Callee

    /// Accept an incoming call.
    public func acceptCall(callId: UUID) async throws {
        guard var record = calls[callId], record.state == .incoming else { return }
        record.state = .connected
        calls[callId] = record
        frameSequence[callId] = 0

        let wire = VoiceControlWire(call_id: callId, from_uhid: sender.localUhid, signal_type: "accept")
        var pkt = MeshPacket(type: .voiceSignaling, sourceUhid: sender.localUhid, destinationUhid: record.remoteUhid, priority: 32)
        pkt.payload = encodeJSON(wire)
        _ = await sender.send(pkt, nextHopUhid: record.remoteUhid)
        onCallStateChanged?(callId, .connected)
    }

    /// Hang up (either side).
    public func hangUp(callId: UUID) async throws {
        guard let record = calls[callId] else { return }
        let wire = VoiceControlWire(call_id: callId, from_uhid: sender.localUhid, signal_type: "hangup")
        var pkt = MeshPacket(type: .voiceSignaling, sourceUhid: sender.localUhid, destinationUhid: record.remoteUhid, priority: 32)
        pkt.payload = encodeJSON(wire)
        _ = await sender.send(pkt, nextHopUhid: record.remoteUhid)
        calls.removeValue(forKey: callId)
        frameSequence.removeValue(forKey: callId)
        onCallStateChanged?(callId, .ended)
    }

    // MARK: – Frame sending

    /// Send an encoded audio frame.
    public func sendFrame(callId: UUID, encodedAudio: Data, isSilence: Bool) async throws {
        guard let record = calls[callId], record.state == .connected else { return }
        let seq = frameSequence[callId, default: 0]
        frameSequence[callId] = seq &+ 1
        let tsMs = Int64(Date().timeIntervalSince1970 * 1000)

        var pkt = MeshPacket(type: .voiceCall, sourceUhid: sender.localUhid, destinationUhid: record.remoteUhid, priority: 64)
        pkt.payload = encodeVoiceFrame(callId: callId, sequence: seq, timestampMs: tsMs, isSilence: isSilence, audio: encodedAudio)
        _ = await sender.send(pkt, nextHopUhid: record.remoteUhid)
    }

    // MARK: – Inbound dispatch

    public func handlePacket(_ packet: MeshPacket) async throws {
        switch packet.type {
        case .voiceSignaling: await handleSignaling(packet)
        case .voiceCall:      handleFrame(packet)
        default: break
        }
    }

    // MARK: – Private

    private func handleSignaling(_ packet: MeshPacket) async {
        // Offer — has codecs field, no signal_type
        if let offer = decodeJSON(VoiceOfferWire.self, from: packet.payload) {
            calls[offer.call_id] = VoiceCallRecord(callId: offer.call_id, remoteUhid: packet.sourceUhid, state: .incoming)
            onIncomingCall?(offer.call_id, packet.sourceUhid, offer.codecs, offer.sample_rate_hz)
            return
        }
        // Control messages (accept / hangup)
        if let ctrl = decodeJSON(VoiceControlWire.self, from: packet.payload) {
            switch ctrl.signal_type {
            case "accept":
                if var r = calls[ctrl.call_id] {
                    r.state = .connected
                    calls[ctrl.call_id] = r
                    onCallStateChanged?(ctrl.call_id, .connected)
                }
            case "hangup":
                calls.removeValue(forKey: ctrl.call_id)
                frameSequence.removeValue(forKey: ctrl.call_id)
                onCallStateChanged?(ctrl.call_id, .ended)
            default: break
            }
        }
    }

    private func handleFrame(_ packet: MeshPacket) {
        guard let (callId, _, tsMs, isSilence, audio) = decodeVoiceFrame(packet.payload) else { return }
        onFrameReceived?(callId, audio, isSilence, tsMs)
    }
}

// ─── Internal model ───────────────────────────────────────

private struct VoiceCallRecord: Sendable {
    var callId: UUID
    var remoteUhid: String
    var state: VoiceCallState
}

// ─── JSON wire types ──────────────────────────────────────

private struct VoiceOfferWire: Codable {
    @LowercaseUUIDCoding var call_id: UUID
    let from_uhid: String
    let codecs: [String]
    let sample_rate_hz: Int
}

/// Generic control message — accept, hangup, etc.
private struct VoiceControlWire: Codable {
    @LowercaseUUIDCoding var call_id: UUID
    let from_uhid: String
    let signal_type: String
}

// ─── Binary VoiceFrame helpers ────────────────────────────

/// Encode VoiceFrame: [16 CallId BE][4 Seq LE][8 TsMs LE][1 IsSilence][N Audio]
private func encodeVoiceFrame(callId: UUID, sequence: UInt32, timestampMs: Int64, isSilence: Bool, audio: Data) -> Data {
    var buf = Data(capacity: 16 + 4 + 8 + 1 + audio.count)
    var uuidBytes = callId.uuid
    withUnsafeBytes(of: &uuidBytes) { buf.append(contentsOf: $0) }
    var seq = sequence.littleEndian
    withUnsafeBytes(of: &seq) { buf.append(contentsOf: $0) }
    var ts = timestampMs.littleEndian
    withUnsafeBytes(of: &ts) { buf.append(contentsOf: $0) }
    buf.append(isSilence ? 1 : 0)
    buf.append(audio)
    return buf
}

private func decodeVoiceFrame(_ data: Data) -> (UUID, UInt32, Int64, Bool, Data)? {
    guard data.count >= 29 else { return nil }
    let callId = UUID(uuid: data.subdata(in: 0..<16).withUnsafeBytes { $0.load(as: uuid_t.self) })
    let seq    = UInt32(littleEndian: data.subdata(in: 16..<20).withUnsafeBytes { $0.load(as: UInt32.self) })
    let tsMs   = Int64(littleEndian: data.subdata(in: 20..<28).withUnsafeBytes { $0.load(as: Int64.self) })
    let isSilence = data[28] != 0
    return (callId, seq, tsMs, isSilence, data.subdata(in: 29..<data.count))
}

// ─── JSON helpers ─────────────────────────────────────────

private func encodeJSON<T: Encodable>(_ value: T) -> Data {
    (try? JSONEncoder().encode(value)) ?? Data()
}

private func decodeJSON<T: Decodable>(_ type: T.Type, from data: Data) -> T? {
    try? JSONDecoder().decode(type, from: data)
}
