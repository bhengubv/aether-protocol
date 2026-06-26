// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherNetProtocol

private let LOCAL = "alice"

/// Build a WatchSync packet from `from` with a JSON payload.
private func watchSyncPkt(from: String, to: String, payload: Data) -> MeshPacket {
    var pkt = MeshPacket(type: .watchSync, sourceUhid: from, destinationUhid: to, priority: 32)
    pkt.payload = payload
    return pkt
}

/// Build a WatchReaction packet.
private func watchReactionPkt(from: String, to: String, payload: Data) -> MeshPacket {
    var pkt = MeshPacket(type: .watchReaction, sourceUhid: from, destinationUhid: to, priority: 16)
    pkt.payload = payload
    return pkt
}

/// Returns a JSON payload for an inbound watch invite.
private func invitePayload(sessionId: UUID, hostUhid: String, mediaUrl: String, members: [String]) -> Data {
    let membersJSON = members.map { "\"\($0)\"" }.joined(separator: ",")
    let s = """
    {"session_id":"\(sessionId.uuidString.lowercased())","host_uhid":"\(hostUhid)","media_url":"\(mediaUrl)","members":[\(membersJSON)],"signal_type":"watch_invite"}
    """
    return s.data(using: .utf8)!
}

/// Returns a JSON payload for a play control.
private func playPayload(sessionId: UUID, fromUhid: String, positionMs: Int64) -> Data {
    let nowMs = Int64(Date().timeIntervalSince1970 * 1000)
    let s = """
    {"session_id":"\(sessionId.uuidString.lowercased())","from_uhid":"\(fromUhid)","position_ms":\(positionMs),"sent_at_ms":\(nowMs),"signal_type":"watch_play"}
    """
    return s.data(using: .utf8)!
}

/// Returns a JSON payload for a pause control.
private func pausePayload(sessionId: UUID, fromUhid: String, positionMs: Int64) -> Data {
    let s = """
    {"session_id":"\(sessionId.uuidString.lowercased())","from_uhid":"\(fromUhid)","position_ms":\(positionMs),"signal_type":"watch_pause"}
    """
    return s.data(using: .utf8)!
}

/// Returns a JSON payload for a seek control.
private func seekPayload(sessionId: UUID, fromUhid: String, positionMs: Int64) -> Data {
    let nowMs = Int64(Date().timeIntervalSince1970 * 1000)
    let s = """
    {"session_id":"\(sessionId.uuidString.lowercased())","from_uhid":"\(fromUhid)","position_ms":\(positionMs),"sent_at_ms":\(nowMs),"signal_type":"watch_seek"}
    """
    return s.data(using: .utf8)!
}

/// Returns a JSON payload for a reaction message.
private func reactionPayload(sessionId: UUID, fromUhid: String, emoji: String) -> Data {
    let nowMs = Int64(Date().timeIntervalSince1970 * 1000)
    let s = """
    {"session_id":"\(sessionId.uuidString.lowercased())","from_uhid":"\(fromUhid)","emoji":"\(emoji)","sent_at_ms":\(nowMs)}
    """
    return s.data(using: .utf8)!
}

// ── Tests ──────────────────────────────────────────────────────────────────────

final class WatchTogetherServiceTests: XCTestCase {

    // MARK: – inviteToSession

    func test_inviteToSession_returnsNonNilSessionId() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)
        let sid = try await svc.inviteToSession(toUhids: ["bob"], mediaUrl: "https://example.com/stream")
        XCTAssertNotEqual(sid, UUID(uuidString: "00000000-0000-0000-0000-000000000000")!)
    }

    func test_inviteToSession_sendsWatchSyncToEachInvitee() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)
        _ = try await svc.inviteToSession(toUhids: ["bob", "carol"], mediaUrl: "https://example.com/stream")
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 2, "invite must send watchSync to each invitee")
        let targets = Set(unicasts.map { $0.nextHopUhid })
        XCTAssertTrue(targets.contains("bob"))
        XCTAssertTrue(targets.contains("carol"))
        for u in unicasts {
            XCTAssertEqual(u.packet.type, .watchSync)
        }
    }

    func test_inviteToSession_payloadContainsSessionId() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)
        let sid = try await svc.inviteToSession(toUhids: ["bob"], mediaUrl: "https://example.com/stream")
        let body = String(data: sender.unicasts()[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains(sid.uuidString.lowercased()), "invite payload must contain session id")
    }

    // MARK: – play

    func test_play_sendsWatchSyncToAllMembers() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)
        let sid = try await svc.inviteToSession(toUhids: ["bob"], mediaUrl: "https://example.com/stream")
        sender.clear()

        try await svc.play(sessionId: sid, positionMs: 5000)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1, "play must send watchSync to each non-local member")
        XCTAssertEqual(unicasts[0].packet.type, .watchSync)
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("watch_play"), "play payload must contain signal_type=watch_play")
    }

    // MARK: – pause

    func test_pause_sendsWatchSyncToAllMembers() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)
        let sid = try await svc.inviteToSession(toUhids: ["bob"], mediaUrl: "https://example.com/stream")
        sender.clear()

        try await svc.pause(sessionId: sid, positionMs: 12000)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        XCTAssertEqual(unicasts[0].packet.type, .watchSync)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("watch_pause"), "pause payload must contain signal_type=watch_pause")
    }

    // MARK: – seek

    func test_seek_sendsWatchSyncToAllMembers() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)
        let sid = try await svc.inviteToSession(toUhids: ["bob"], mediaUrl: "https://example.com/stream")
        sender.clear()

        try await svc.seek(sessionId: sid, positionMs: 30000)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("watch_seek"), "seek payload must contain signal_type=watch_seek")
        XCTAssertTrue(body.contains("30000"), "seek payload must contain the requested position")
    }

    // MARK: – setSpeed

    func test_setSpeed_sendsWatchSyncToAllMembers() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)
        let sid = try await svc.inviteToSession(toUhids: ["bob"], mediaUrl: "https://example.com/stream")
        sender.clear()

        try await svc.setSpeed(sessionId: sid, speed: 1.5)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1)
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("watch_speed"), "speed payload must contain signal_type=watch_speed")
        XCTAssertTrue(body.contains("1.5"), "speed payload must contain the speed value")
    }

    // MARK: – sendReaction

    func test_sendReaction_sendsWatchReactionToAllMembers() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)
        let sid = try await svc.inviteToSession(toUhids: ["bob", "carol"], mediaUrl: "https://example.com/stream")
        sender.clear()

        try await svc.sendReaction(sessionId: sid, emoji: "🔥")

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 2, "reaction must reach both members")
        for u in unicasts {
            XCTAssertEqual(u.packet.type, .watchReaction)
            let body = String(data: u.packet.payload, encoding: .utf8) ?? ""
            XCTAssertTrue(body.contains("🔥"), "reaction payload must contain emoji")
        }
    }

    // MARK: – handlePacket — inbound invite

    func test_handlePacket_invite_firesOnInviteReceived() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)

        let invitedSessionId = Locked<UUID?>(nil)
        let invitedHost = Locked("")
        let invitedMediaUrl = Locked("")
        await svc.setOnInviteReceived { sid, host, url in
            invitedSessionId.value = sid
            invitedHost.value = host
            invitedMediaUrl.value = url
        }

        let sid = UUID()
        let pkt = watchSyncPkt(from: "bob", to: LOCAL,
                               payload: invitePayload(sessionId: sid, hostUhid: "bob",
                                                       mediaUrl: "https://example.com/stream",
                                                       members: ["bob", LOCAL]))
        try await svc.handlePacket(pkt)

        XCTAssertEqual(invitedSessionId.value, sid)
        XCTAssertEqual(invitedHost.value, "bob")
        XCTAssertEqual(invitedMediaUrl.value, "https://example.com/stream")
    }

    // MARK: – handlePacket — inbound play

    func test_handlePacket_play_firesOnPlaybackStateChanged_playing() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)

        let firedIsPlaying = Locked<Bool?>(nil)
        let firedPositionMs = Locked<Int64?>(nil)
        await svc.setOnPlaybackStateChanged { _, isPlaying, posMs in
            firedIsPlaying.value = isPlaying
            firedPositionMs.value = posMs
        }

        let sid = UUID()
        // First handle the invite so the session exists (for speed lookup during RTT compensation).
        let invitePkt = watchSyncPkt(from: "bob", to: LOCAL,
                                     payload: invitePayload(sessionId: sid, hostUhid: "bob",
                                                             mediaUrl: "https://example.com/m",
                                                             members: ["bob", LOCAL]))
        try await svc.handlePacket(invitePkt)

        let pkt = watchSyncPkt(from: "bob", to: LOCAL,
                               payload: playPayload(sessionId: sid, fromUhid: "bob", positionMs: 5000))
        try await svc.handlePacket(pkt)

        XCTAssertEqual(firedIsPlaying.value, true)
        // RTT compensation adds (nowMs - sentAtMs) * speed. With sent_at_ms ≈ nowMs, compensation ≈ 0.
        XCTAssertGreaterThanOrEqual(firedPositionMs.value ?? 0, 5000, "compensated position must be >= requested position")
    }

    // MARK: – handlePacket — inbound pause

    func test_handlePacket_pause_firesOnPlaybackStateChanged_paused() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)

        let firedIsPlaying = Locked<Bool?>(nil)
        let firedPositionMs = Locked<Int64?>(nil)
        await svc.setOnPlaybackStateChanged { _, isPlaying, posMs in
            firedIsPlaying.value = isPlaying
            firedPositionMs.value = posMs
        }

        let sid = UUID()
        let pkt = watchSyncPkt(from: "bob", to: LOCAL,
                               payload: pausePayload(sessionId: sid, fromUhid: "bob", positionMs: 15000))
        try await svc.handlePacket(pkt)

        XCTAssertEqual(firedIsPlaying.value, false)
        XCTAssertEqual(firedPositionMs.value, 15000, "pause position must be forwarded exactly (no RTT compensation)")
    }

    // MARK: – handlePacket — inbound seek

    func test_handlePacket_seek_firesOnPlaybackStateChanged() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)

        let firedPositionMs = Locked<Int64?>(nil)
        await svc.setOnPlaybackStateChanged { _, _, posMs in firedPositionMs.value = posMs }

        let sid = UUID()
        let pkt = watchSyncPkt(from: "bob", to: LOCAL,
                               payload: seekPayload(sessionId: sid, fromUhid: "bob", positionMs: 30000))
        try await svc.handlePacket(pkt)

        XCTAssertGreaterThanOrEqual(firedPositionMs.value ?? 0, 30000, "seek compensated position must be >= requested position")
    }

    // MARK: – handlePacket — inbound reaction

    func test_handlePacket_reaction_firesOnReactionReceived() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = WatchTogetherService(sender: sender)

        let firedEmoji = Locked("")
        let firedFrom = Locked("")
        await svc.setOnReactionReceived { _, from, emoji in
            firedFrom.value = from
            firedEmoji.value = emoji
        }

        let sid = UUID()
        let pkt = watchReactionPkt(from: "bob", to: LOCAL,
                                   payload: reactionPayload(sessionId: sid, fromUhid: "bob", emoji: "❤️"))
        try await svc.handlePacket(pkt)

        XCTAssertEqual(firedFrom.value, "bob")
        XCTAssertEqual(firedEmoji.value, "❤️")
    }
}
