// SPDX-License-Identifier: MIT
// NOTE: CI on Linux is the verification gate.

import Foundation

// ─── WatchTogetherService ─────────────────────────────────

/// Synchronised watch-together session for shared media playback.
///
/// Playback control and invite messages use packet type `.watchSync`.
/// Reaction messages use packet type `.watchReaction`.
/// All payloads are JSON (Codable, snake_case keys).
///
/// RTT compensation for play/seek:
///   adjustedPositionMs = positionMs + Int64(Double(nowMs - sentAtMs) * playbackSpeed)
public actor WatchTogetherService {
    private let sender: any MeshSender

    private var sessions: [UUID: WatchSession] = [:]

    public var onInviteReceived: (@Sendable (UUID, String, String) -> Void)?
    /// (sessionId, isPlaying, compensatedPositionMs)
    public var onPlaybackStateChanged: (@Sendable (UUID, Bool, Int64) -> Void)?
    /// (sessionId, fromUhid, emoji)
    public var onReactionReceived: (@Sendable (UUID, String, String) -> Void)?
    public var onMemberJoined: (@Sendable (UUID, String) -> Void)?
    public var onMemberLeft: (@Sendable (UUID, String) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    // MARK: – Callbacks

    public func setOnInviteReceived(_ cb: (@Sendable (UUID, String, String) -> Void)?) {
        onInviteReceived = cb
    }
    public func setOnPlaybackStateChanged(_ cb: (@Sendable (UUID, Bool, Int64) -> Void)?) {
        onPlaybackStateChanged = cb
    }
    public func setOnReactionReceived(_ cb: (@Sendable (UUID, String, String) -> Void)?) {
        onReactionReceived = cb
    }
    public func setOnMemberJoined(_ cb: (@Sendable (UUID, String) -> Void)?) {
        onMemberJoined = cb
    }
    public func setOnMemberLeft(_ cb: (@Sendable (UUID, String) -> Void)?) {
        onMemberLeft = cb
    }

    // MARK: – Session management

    /// Create and invite peers to a watch-together session.
    public func inviteToSession(toUhids: [String], mediaUrl: String) async throws -> UUID {
        let sessionId = UUID()
        var members = [sender.localUhid]
        for u in toUhids where !members.contains(u) { members.append(u) }
        sessions[sessionId] = WatchSession(sessionId: sessionId, hostUhid: sender.localUhid, mediaUrl: mediaUrl, members: members, isPlaying: false, positionMs: 0, playbackSpeed: 1.0)

        let wire = WatchInviteWire(session_id: sessionId, host_uhid: sender.localUhid, media_url: mediaUrl, members: members)
        for uhid in toUhids {
            await sendSync(encodeJSON(wire), toUhid: uhid)
        }
        return sessionId
    }

    // MARK: – Playback controls

    /// Begin or resume playback — broadcasts to all session members.
    public func play(sessionId: UUID, positionMs: Int64) async throws {
        guard var session = sessions[sessionId] else { return }
        session.isPlaying = true
        session.positionMs = positionMs
        sessions[sessionId] = session

        let now = Int64(Date().timeIntervalSince1970 * 1000)
        let wire = WatchPlayWire(session_id: sessionId, from_uhid: sender.localUhid, position_ms: positionMs, sent_at_ms: now)
        await broadcastSync(sessionId: sessionId, payload: encodeJSON(wire))
    }

    /// Pause playback.
    public func pause(sessionId: UUID, positionMs: Int64) async throws {
        guard var session = sessions[sessionId] else { return }
        session.isPlaying = false
        session.positionMs = positionMs
        sessions[sessionId] = session

        let wire = WatchPauseWire(session_id: sessionId, from_uhid: sender.localUhid, position_ms: positionMs)
        await broadcastSync(sessionId: sessionId, payload: encodeJSON(wire))
    }

    /// Seek to a position.
    public func seek(sessionId: UUID, positionMs: Int64) async throws {
        guard var session = sessions[sessionId] else { return }
        session.positionMs = positionMs
        sessions[sessionId] = session

        let now = Int64(Date().timeIntervalSince1970 * 1000)
        let wire = WatchSeekWire(session_id: sessionId, from_uhid: sender.localUhid, position_ms: positionMs, sent_at_ms: now)
        await broadcastSync(sessionId: sessionId, payload: encodeJSON(wire))
    }

    /// Change playback speed.
    public func setSpeed(sessionId: UUID, speed: Double) async throws {
        guard var session = sessions[sessionId] else { return }
        session.playbackSpeed = speed
        sessions[sessionId] = session

        let wire = WatchSpeedWire(session_id: sessionId, from_uhid: sender.localUhid, speed: speed)
        await broadcastSync(sessionId: sessionId, payload: encodeJSON(wire))
    }

    // MARK: – Reactions

    /// Send an emoji reaction to all session members.
    public func sendReaction(sessionId: UUID, emoji: String) async throws {
        guard let session = sessions[sessionId] else { return }
        let now = Int64(Date().timeIntervalSince1970 * 1000)
        let wire = WatchReactionWire(session_id: sessionId, from_uhid: sender.localUhid, emoji: emoji, sent_at_ms: now)
        let payload = encodeJSON(wire)
        for uhid in session.members where uhid != sender.localUhid {
            var pkt = MeshPacket(type: .watchReaction, sourceUhid: sender.localUhid, destinationUhid: uhid, priority: 16)
            pkt.payload = payload
            _ = await sender.send(pkt, nextHopUhid: uhid)
        }
    }

    // MARK: – Inbound dispatch

    public func handlePacket(_ packet: MeshPacket) async throws {
        switch packet.type {
        case .watchSync:     handleSync(packet)
        case .watchReaction: handleReaction(packet)
        default: break
        }
    }

    // MARK: – Private

    private func handleSync(_ packet: MeshPacket) {
        let nowMs = Int64(Date().timeIntervalSince1970 * 1000)

        if let invite = decodeJSON(WatchInviteWire.self, from: packet.payload) {
            if sessions[invite.session_id] == nil {
                sessions[invite.session_id] = WatchSession(sessionId: invite.session_id, hostUhid: invite.host_uhid, mediaUrl: invite.media_url, members: invite.members, isPlaying: false, positionMs: 0, playbackSpeed: 1.0)
            }
            onInviteReceived?(invite.session_id, invite.host_uhid, invite.media_url)
            return
        }
        if let play = decodeJSON(WatchPlayWire.self, from: packet.payload) {
            let speed = sessions[play.session_id]?.playbackSpeed ?? 1.0
            let compensated = play.position_ms + Int64(Double(nowMs - play.sent_at_ms) * speed)
            if var s = sessions[play.session_id] { s.isPlaying = true; s.positionMs = compensated; sessions[s.sessionId] = s }
            onPlaybackStateChanged?(play.session_id, true, compensated)
            return
        }
        if let pause = decodeJSON(WatchPauseWire.self, from: packet.payload) {
            if var s = sessions[pause.session_id] { s.isPlaying = false; s.positionMs = pause.position_ms; sessions[s.sessionId] = s }
            onPlaybackStateChanged?(pause.session_id, false, pause.position_ms)
            return
        }
        if let seek = decodeJSON(WatchSeekWire.self, from: packet.payload) {
            let speed = sessions[seek.session_id]?.playbackSpeed ?? 1.0
            let compensated = seek.position_ms + Int64(Double(nowMs - seek.sent_at_ms) * speed)
            let playing = sessions[seek.session_id]?.isPlaying ?? false
            if var s = sessions[seek.session_id] { s.positionMs = compensated; sessions[s.sessionId] = s }
            onPlaybackStateChanged?(seek.session_id, playing, compensated)
            return
        }
        if let speedMsg = decodeJSON(WatchSpeedWire.self, from: packet.payload) {
            if var s = sessions[speedMsg.session_id] { s.playbackSpeed = speedMsg.speed; sessions[s.sessionId] = s }
        }
    }

    private func handleReaction(_ packet: MeshPacket) {
        guard let wire = decodeJSON(WatchReactionWire.self, from: packet.payload) else { return }
        onReactionReceived?(wire.session_id, wire.from_uhid, wire.emoji)
    }

    private func broadcastSync(sessionId: UUID, payload: Data) async {
        guard let session = sessions[sessionId] else { return }
        for uhid in session.members where uhid != sender.localUhid {
            await sendSync(payload, toUhid: uhid)
        }
    }

    private func sendSync(_ payload: Data, toUhid: String) async {
        var pkt = MeshPacket(type: .watchSync, sourceUhid: sender.localUhid, destinationUhid: toUhid, priority: 32)
        pkt.payload = payload
        _ = await sender.send(pkt, nextHopUhid: toUhid)
    }
}

// ─── Internal model ───────────────────────────────────────

private struct WatchSession: Sendable {
    var sessionId: UUID
    var hostUhid: String
    var mediaUrl: String
    var members: [String]
    var isPlaying: Bool
    var positionMs: Int64
    var playbackSpeed: Double
}

// ─── JSON wire types ──────────────────────────────────────

private struct WatchInviteWire: Codable {
    @LowercaseUUIDCoding var session_id: UUID
    let host_uhid: String
    let media_url: String
    let members: [String]
    let signal_type: String = "watch_invite"
    private enum CodingKeys: String, CodingKey {
        case session_id, host_uhid, media_url, members, signal_type
    }
}

private struct WatchPlayWire: Codable {
    @LowercaseUUIDCoding var session_id: UUID
    let from_uhid: String
    let position_ms: Int64
    let sent_at_ms: Int64
    let signal_type: String = "watch_play"
    private enum CodingKeys: String, CodingKey {
        case session_id, from_uhid, position_ms, sent_at_ms, signal_type
    }
}

private struct WatchPauseWire: Codable {
    @LowercaseUUIDCoding var session_id: UUID
    let from_uhid: String
    let position_ms: Int64
    let signal_type: String = "watch_pause"
    private enum CodingKeys: String, CodingKey {
        case session_id, from_uhid, position_ms, signal_type
    }
}

private struct WatchSeekWire: Codable {
    @LowercaseUUIDCoding var session_id: UUID
    let from_uhid: String
    let position_ms: Int64
    let sent_at_ms: Int64
    let signal_type: String = "watch_seek"
    private enum CodingKeys: String, CodingKey {
        case session_id, from_uhid, position_ms, sent_at_ms, signal_type
    }
}

private struct WatchSpeedWire: Codable {
    @LowercaseUUIDCoding var session_id: UUID
    let from_uhid: String
    let speed: Double
    let signal_type: String = "watch_speed"
    private enum CodingKeys: String, CodingKey {
        case session_id, from_uhid, speed, signal_type
    }
}

private struct WatchReactionWire: Codable {
    @LowercaseUUIDCoding var session_id: UUID
    let from_uhid: String
    let emoji: String
    let sent_at_ms: Int64
}

private func encodeJSON<T: Encodable>(_ value: T) -> Data {
    (try? JSONEncoder().encode(value)) ?? Data()
}

private func decodeJSON<T: Decodable>(_ type: T.Type, from data: Data) -> T? {
    try? JSONDecoder().decode(type, from: data)
}
