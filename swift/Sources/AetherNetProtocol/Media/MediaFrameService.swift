// SPDX-License-Identifier: MIT
// NOTE: CI on Linux (and macOS) is the verification gate; `swift test` runs on the Mac.

import Foundation

// ─── VoicePttService ──────────────────────────────────────

/// Binds ``PacketType/voicePtt`` (15) to the mesh: directed push-to-talk audio frames + an inbound
/// callback. Directed (unicast) delivery only — a PTT frame is always addressed to one peer.
///
/// Mirrors the C# `VoicePttService`:
///   - ``sendFrame(peerUhid:frame:)`` emits a directed VoicePtt(15) packet and returns the send result.
///   - ``handle(_:)`` parses an inbound packet, fires ``onFrameReceived``, and returns `true`;
///     a wrong-type packet or a body shorter than the 29-byte header returns `false`.
public actor VoicePttService {
    private let sender: any MeshSender

    /// (frame, fromUhid) — fired for each accepted inbound VoicePtt frame.
    public var onFrameReceived: (@Sendable (VoicePttFrame, String) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnFrameReceived(_ cb: (@Sendable (VoicePttFrame, String) -> Void)?) {
        onFrameReceived = cb
    }

    /// Send a directed push-to-talk audio frame to `peerUhid`. Returns the mesh send result.
    @discardableResult
    public func sendFrame(peerUhid: String, frame: VoicePttFrame) async -> Bool {
        var pkt = MeshPacket(
            type: .voicePtt,
            sourceUhid: sender.localUhid,
            destinationUhid: peerUhid,
            ttl: ProtocolConstants.defaultTtl,
            priority: 64
        )
        pkt.payload = MediaFrameCodec.serializeVoicePtt(frame)
        return await sender.send(pkt, nextHopUhid: peerUhid)
    }

    /// Handle an inbound packet. Returns `true` if it was a well-formed VoicePtt frame (callback
    /// fired); `false` for a wrong-type packet or a body too short to hold the 29-byte header.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .voicePtt else { return false }
        guard let frame = MediaFrameCodec.deserializeVoicePtt(packet.payload) else { return false }
        onFrameReceived?(frame, packet.sourceUhid)
        return true
    }
}

// ─── ScreenShareService ───────────────────────────────────

/// Binds ``PacketType/screenShare`` (32) to the mesh: directed screen-share video frames + an
/// inbound callback. Directed (unicast) delivery only.
///
/// Mirrors the C# `ScreenShareService`:
///   - ``sendFrame(peerUhid:frame:)`` emits a directed ScreenShare(32) packet and returns the send result.
///   - ``handle(_:)`` parses an inbound packet, fires ``onFrameReceived``, and returns `true`;
///     a wrong-type packet or a body shorter than the 29-byte header returns `false`.
public actor ScreenShareService {
    private let sender: any MeshSender

    /// (frame, fromUhid) — fired for each accepted inbound ScreenShare frame.
    public var onFrameReceived: (@Sendable (ScreenShareFrame, String) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnFrameReceived(_ cb: (@Sendable (ScreenShareFrame, String) -> Void)?) {
        onFrameReceived = cb
    }

    /// Send a directed screen-share video frame to `peerUhid`. Returns the mesh send result.
    @discardableResult
    public func sendFrame(peerUhid: String, frame: ScreenShareFrame) async -> Bool {
        var pkt = MeshPacket(
            type: .screenShare,
            sourceUhid: sender.localUhid,
            destinationUhid: peerUhid,
            ttl: ProtocolConstants.defaultTtl,
            priority: 64
        )
        pkt.payload = MediaFrameCodec.serializeScreenShare(frame)
        return await sender.send(pkt, nextHopUhid: peerUhid)
    }

    /// Handle an inbound packet. Returns `true` if it was a well-formed ScreenShare frame (callback
    /// fired); `false` for a wrong-type packet or a body too short to hold the 29-byte header.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .screenShare else { return false }
        guard let frame = MediaFrameCodec.deserializeScreenShare(packet.payload) else { return false }
        onFrameReceived?(frame, packet.sourceUhid)
        return true
    }
}
