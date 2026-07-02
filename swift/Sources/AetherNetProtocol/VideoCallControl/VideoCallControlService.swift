// SPDX-License-Identifier: MIT

import Foundation

// ─── VideoCallStateChanged ────────────────────────────────

/// Event raised when a video call-control signal arrives from a peer.
///
/// Mirrors C# `VideoCallStateChanged`.
public struct VideoCallStateChanged: Sendable, Equatable {
    /// Id of the call the signal refers to.
    public let callId: UUID
    /// The control verb received ("ring" / "accept" / "decline" / "hangup").
    public let action: String
    /// UHID of the peer that sent the signal.
    public let fromUhid: String

    public init(callId: UUID, action: String, fromUhid: String) {
        self.callId = callId
        self.action = action
        self.fromUhid = fromUhid
    }
}

// ─── VideoCallControlService ──────────────────────────────

/// Video call-control over ``PacketType/videoCall`` (PacketType 27) — directed
/// ring/accept/decline/hangup signalling between two peers. The caller rings a peer
/// (minting a call id); either side then accepts, declines, or hangs up. Inbound
/// signals surface via ``onCallStateChanged``. The media plane (SDP/ICE + frames) is
/// handled separately by the streaming ``VideoCallService``.
///
/// This is the caller-intent layer, mirroring how ``VoiceCallService`` carries voice
/// call-control. Mirrors C# `VideoCallControlService`.
public actor VideoCallControlService {
    private let sender: any MeshSender

    /// Raised when a call-control signal is received from a peer.
    public var onCallStateChanged: (@Sendable (VideoCallStateChanged) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnCallStateChanged(_ callback: (@Sendable (VideoCallStateChanged) -> Void)?) {
        onCallStateChanged = callback
    }

    // MARK: – Originator / responder

    /// Ring `peerUhid`: mint a call id and directed-send a "ring". Returns the new call id.
    @discardableResult
    public func ring(_ peerUhid: String) async -> UUID {
        let callId = UUID()
        _ = await sendControl(callId: callId, peerUhid: peerUhid, action: "ring")
        return callId
    }

    /// Directed-send an "accept" for `callId` to `peerUhid`. Returns delivery success.
    @discardableResult
    public func accept(_ callId: UUID, peerUhid: String) async -> Bool {
        await sendControl(callId: callId, peerUhid: peerUhid, action: "accept")
    }

    /// Directed-send a "decline" for `callId` to `peerUhid`. Returns delivery success.
    @discardableResult
    public func decline(_ callId: UUID, peerUhid: String) async -> Bool {
        await sendControl(callId: callId, peerUhid: peerUhid, action: "decline")
    }

    /// Directed-send a "hangup" for `callId` to `peerUhid`. Returns delivery success.
    @discardableResult
    public func hangup(_ callId: UUID, peerUhid: String) async -> Bool {
        await sendControl(callId: callId, peerUhid: peerUhid, action: "hangup")
    }

    private func sendControl(callId: UUID, peerUhid: String, action: String) async -> Bool {
        guard !peerUhid.isEmpty else { return false }

        let body = encodeVideoCallControlWire(
            callId: callId,
            action: action,
            sentAtMs: Int64(Date().timeIntervalSince1970 * 1000)
        )

        let packet = MeshPacket(
            type: .videoCall,
            sourceUhid: sender.localUhid,
            destinationUhid: peerUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: body
        )

        return await sender.send(packet, nextHopUhid: peerUhid)
    }

    // MARK: – Inbound dispatch

    /// Process an incoming ``PacketType/videoCall`` packet: parse and fire
    /// ``onCallStateChanged``. Returns false for the wrong packet type or a malformed
    /// payload (including an empty action), true once the event has been surfaced.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .videoCall else { return false }

        guard let body = parseVideoCallControlWire(packet.payload), !body.action.isEmpty else {
            return false
        }

        onCallStateChanged?(VideoCallStateChanged(
            callId: body.callId,
            action: body.action,
            fromUhid: packet.sourceUhid
        ))
        return true
    }
}

// ─── VideoCallControl wire (PacketType 27) ───
//
// Serialises to snake_case keys, field order call_id, action, sent_at_ms, no whitespace,
// GUID lowercase-dashed, action an ASCII verb, sent_at_ms a bare integer. This is the
// byte-identity gate (fixtures/videocall/vectors.json).

private struct VideoCallControlWire: Codable {
    @LowercaseUUIDCoding var call_id: UUID
    let action: String
    let sent_at_ms: Int64
    // Lock the wire field order explicitly (call_id, action, sent_at_ms) — matches the
    // convention used by the other wrapped wire structs so the byte-identity gate never
    // depends on Codable's synthesis order.
    private enum CodingKeys: String, CodingKey {
        case call_id, action, sent_at_ms
    }
}

// Foundation's JSONEncoder does NOT emit keys in a deterministic declaration order — with
// 3+ fields it hash-reorders them, breaking cross-language byte-identity. So the wire JSON
// is built by hand in the exact field order, mirroring the other language ports (and the
// Swift ChannelMessageService). Decode still uses JSONDecoder below, which is order-independent.
private func jsonEscaped(_ s: String) -> String {
    var out = "\""
    for scalar in s.unicodeScalars {
        switch scalar {
        case "\"": out += "\\\""
        case "\\": out += "\\\\"
        case "\n": out += "\\n"
        case "\r": out += "\\r"
        case "\t": out += "\\t"
        default:
            if scalar.value < 0x20 { out += String(format: "\\u%04x", scalar.value) }
            else { out.unicodeScalars.append(scalar) }
        }
    }
    out += "\""
    return out
}

private func encodeVideoCallControlWire(callId: UUID, action: String, sentAtMs: Int64) -> Data {
    let json = "{\"call_id\":\"\(callId.uuidString.lowercased())\","
        + "\"action\":\(jsonEscaped(action)),"
        + "\"sent_at_ms\":\(sentAtMs)}"
    return Data(json.utf8)
}

private func parseVideoCallControlWire(_ data: Data) -> (callId: UUID, action: String, sentAtMs: Int64)? {
    guard let w = try? JSONDecoder().decode(VideoCallControlWire.self, from: data) else { return nil }
    return (w.call_id, w.action, w.sent_at_ms)
}

/// Test-only shim exposing the real ``VideoCallControlWire`` serialization path (the struct
/// itself stays `private`) so the byte-identity vectors in `fixtures/videocall/vectors.json`
/// can be verified.
internal func _videoCallControlWireBytesForTests(callId: UUID, action: String, sentAtMs: Int64) -> Data {
    encodeVideoCallControlWire(callId: callId, action: action, sentAtMs: sentAtMs)
}
