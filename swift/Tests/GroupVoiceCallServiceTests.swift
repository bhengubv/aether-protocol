// SPDX-License-Identifier: MIT
import XCTest
@testable import AetherProtocol

private let LOCAL = "alice"

/// Build a VoiceSignaling packet with a JSON payload.
private func groupSigPkt(from: String, to: String, payload: Data) -> MeshPacket {
    var pkt = MeshPacket(type: .voiceSignaling, sourceUhid: from, destinationUhid: to, priority: 32)
    pkt.payload = payload
    return pkt
}

/// JSON payload for an inbound group invite.
private func groupInvitePayload(callId: UUID, fromUhid: String, codecs: [String], members: [String]) -> Data {
    let codecsJSON = codecs.map { "\"\($0)\"" }.joined(separator: ",")
    let membersJSON = members.map { "\"\($0)\"" }.joined(separator: ",")
    let s = """
    {"call_id":"\(callId.uuidString.lowercased())","from_uhid":"\(fromUhid)","codecs":[\(codecsJSON)],"members":[\(membersJSON)],"signal_type":"group_invite"}
    """
    return s.data(using: .utf8)!
}

/// JSON payload for a group_join or group_leave member message.
private func groupMemberPayload(callId: UUID, uhid: String, signalType: String) -> Data {
    let s = """
    {"call_id":"\(callId.uuidString.lowercased())","uhid":"\(uhid)","signal_type":"\(signalType)"}
    """
    return s.data(using: .utf8)!
}

/// JSON payload for a group_kick message.
private func groupKickPayload(callId: UUID, kickedUhid: String, byUhid: String) -> Data {
    let s = """
    {"call_id":"\(callId.uuidString.lowercased())","kicked_uhid":"\(kickedUhid)","by_uhid":"\(byUhid)","signal_type":"group_kick"}
    """
    return s.data(using: .utf8)!
}

/// Build a binary GroupVoiceFrame packet.
/// Wire: [16 callId BE][4 seq LE][8 tsMs LE][1 isSilence][4 keyGen LE][N audio]
private func groupFramePkt(callId: UUID, isSilence: Bool = false, keyGen: UInt32 = 0, audio: Data) -> MeshPacket {
    var buf = Data(count: 33 + audio.count)
    let uuidBytes = withUnsafeBytes(of: callId.uuid) { Data($0) }
    buf.replaceSubrange(0..<16, with: uuidBytes)
    // seq (16..19), tsMs (20..27) — zero
    buf[28] = isSilence ? 1 : 0
    var kg = keyGen.littleEndian
    withUnsafeBytes(of: kg) { buf.replaceSubrange(29..<33, with: $0) }
    buf.replaceSubrange(33..<(33 + audio.count), with: audio)
    var pkt = MeshPacket(type: .voiceCall, sourceUhid: "bob", destinationUhid: LOCAL, priority: 64)
    pkt.payload = buf
    return pkt
}

// ── Tests ──────────────────────────────────────────────────────────────────────

final class GroupVoiceCallServiceTests: XCTestCase {

    // MARK: – invite

    func test_invite_returnsNonNilCallId() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)
        let callId = try await svc.invite(toUhids: ["bob"], codecs: ["opus"])
        XCTAssertNotEqual(callId, UUID(uuidString: "00000000-0000-0000-0000-000000000000")!)
    }

    func test_invite_sendsGroupInviteSignalingToEachInvitee() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)
        _ = try await svc.invite(toUhids: ["bob", "carol"], codecs: ["opus"])
        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 2, "invite must send voiceSignaling to each invitee")
        let targets = Set(unicasts.map { $0.nextHopUhid })
        XCTAssertTrue(targets.contains("bob"))
        XCTAssertTrue(targets.contains("carol"))
        for u in unicasts {
            XCTAssertEqual(u.packet.type, .voiceSignaling)
            let body = String(data: u.packet.payload, encoding: .utf8) ?? ""
            XCTAssertTrue(body.contains("group_invite"), "invite payload must contain signal_type=group_invite")
        }
    }

    // MARK: – handlePacket — inbound invite

    func test_handlePacket_inboundInvite_firesOnInviteReceived() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)

        var firedCallId: UUID?
        var firedFrom = ""
        var firedCodecs: [String] = []
        await svc.setOnInviteReceived { cid, from, codecs in
            firedCallId = cid
            firedFrom = from
            firedCodecs = codecs
        }

        let callId = UUID()
        let pkt = groupSigPkt(from: "bob", to: LOCAL,
                              payload: groupInvitePayload(callId: callId, fromUhid: "bob",
                                                          codecs: ["opus"], members: ["bob", LOCAL]))
        try await svc.handlePacket(pkt)

        XCTAssertEqual(firedCallId, callId)
        XCTAssertEqual(firedFrom, "bob")
        XCTAssertEqual(firedCodecs, ["opus"])
    }

    // MARK: – join

    func test_join_sendsGroupJoinToAllMembers() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)
        // invite creates the call and adds alice + bob as members
        let callId = try await svc.invite(toUhids: ["bob"], codecs: ["opus"])
        sender.clear()

        try await svc.join(callId: callId)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1, "join must send group_join to each other member")
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("group_join"), "join payload must contain signal_type=group_join")
    }

    func test_handlePacket_inboundJoin_firesOnMemberJoined() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)

        // Establish the call via an inbound invite first.
        let callId = UUID()
        let invitePkt = groupSigPkt(from: "bob", to: LOCAL,
                                    payload: groupInvitePayload(callId: callId, fromUhid: "bob",
                                                                 codecs: ["opus"], members: ["bob", LOCAL]))
        try await svc.handlePacket(invitePkt)

        var joinedMember = ""
        await svc.setOnMemberJoined { _, uhid in joinedMember = uhid }

        let joinPkt = groupSigPkt(from: "carol", to: LOCAL,
                                  payload: groupMemberPayload(callId: callId, uhid: "carol", signalType: "group_join"))
        try await svc.handlePacket(joinPkt)

        XCTAssertEqual(joinedMember, "carol", "onMemberJoined must fire with the joining uhid")
    }

    // MARK: – leave

    func test_leave_sendsGroupLeaveToAllMembers() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)
        let callId = try await svc.invite(toUhids: ["bob"], codecs: ["opus"])
        sender.clear()

        try await svc.leave(callId: callId)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 1, "leave must send group_leave to each other member")
        XCTAssertEqual(unicasts[0].nextHopUhid, "bob")
        let body = String(data: unicasts[0].packet.payload, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("group_leave"), "leave payload must contain signal_type=group_leave")
    }

    func test_handlePacket_inboundLeave_firesOnMemberLeft() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)

        let callId = UUID()
        let invitePkt = groupSigPkt(from: "bob", to: LOCAL,
                                    payload: groupInvitePayload(callId: callId, fromUhid: "bob",
                                                                 codecs: ["opus"], members: ["bob", LOCAL]))
        try await svc.handlePacket(invitePkt)

        var leftMember = ""
        await svc.setOnMemberLeft { _, uhid in leftMember = uhid }

        let leavePkt = groupSigPkt(from: "bob", to: LOCAL,
                                   payload: groupMemberPayload(callId: callId, uhid: "bob", signalType: "group_leave"))
        try await svc.handlePacket(leavePkt)

        XCTAssertEqual(leftMember, "bob", "onMemberLeft must fire with the leaving uhid")
    }

    // MARK: – kick

    func test_kick_removesKickedMemberAndNotifiesAll() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)
        let callId = try await svc.invite(toUhids: ["bob", "carol"], codecs: ["opus"])
        sender.clear()

        try await svc.kick(callId: callId, uhid: "carol")

        let unicasts = sender.unicasts()
        // kick sends to remaining members (bob) + the kicked person (carol)
        XCTAssertGreaterThanOrEqual(unicasts.count, 1, "kick must notify at least the kicked peer")
        for u in unicasts {
            let body = String(data: u.packet.payload, encoding: .utf8) ?? ""
            XCTAssertTrue(body.contains("group_kick"), "kick payload must contain signal_type=group_kick")
            XCTAssertTrue(body.contains("carol"), "kick payload must reference the kicked uhid")
        }
    }

    func test_handlePacket_selfKicked_firesOnMemberLeft_withLocalUhid() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)

        // Receive an invite to join the call.
        let callId = UUID()
        let invitePkt = groupSigPkt(from: "bob", to: LOCAL,
                                    payload: groupInvitePayload(callId: callId, fromUhid: "bob",
                                                                 codecs: ["opus"], members: ["bob", LOCAL]))
        try await svc.handlePacket(invitePkt)

        var leftMember = ""
        await svc.setOnMemberLeft { _, uhid in leftMember = uhid }

        // Bob kicks alice (LOCAL).
        let kickPkt = groupSigPkt(from: "bob", to: LOCAL,
                                  payload: groupKickPayload(callId: callId, kickedUhid: LOCAL, byUhid: "bob"))
        try await svc.handlePacket(kickPkt)

        XCTAssertEqual(leftMember, LOCAL, "onMemberLeft must fire with the local uhid when self is kicked")
    }

    // MARK: – sendFrame

    func test_sendFrame_sendsVoiceCallToAllMembersExceptSelf() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)
        let callId = try await svc.invite(toUhids: ["bob", "carol"], codecs: ["opus"])
        sender.clear()

        let audio = Data([0xDE, 0xAD, 0xBE, 0xEF])
        try await svc.sendFrame(callId: callId, encodedAudio: audio, isSilence: false)

        let unicasts = sender.unicasts()
        XCTAssertEqual(unicasts.count, 2, "sendFrame must reach both non-local members")
        for u in unicasts {
            XCTAssertEqual(u.packet.type, .voiceCall)
            // Wire: [16 callId][4 seq][8 ts][1 isSilence][4 keyGen][N audio] = 33 bytes header
            XCTAssertGreaterThanOrEqual(u.packet.payload.count, 33 + audio.count)
        }
        let targets = Set(unicasts.map { $0.nextHopUhid })
        XCTAssertTrue(targets.contains("bob"))
        XCTAssertTrue(targets.contains("carol"))
        XCTAssertFalse(targets.contains(LOCAL), "sendFrame must not send to self")
    }

    // MARK: – onFrameReceived

    func test_handlePacket_groupVoiceFrame_firesOnFrameReceived() async throws {
        let sender = FakeMeshSender(localUhid: LOCAL)
        let svc = GroupVoiceCallService(sender: sender)

        var frameReceived = false
        var receivedAudio = Data()
        var receivedKeyGen: UInt32 = 0
        await svc.setOnFrameReceived { _, _, audio, _, keyGen, _ in
            frameReceived = true
            receivedAudio = audio
            receivedKeyGen = keyGen
        }

        let callId = UUID()
        let audio = Data([0xAA, 0xBB, 0xCC])
        let framePkt = groupFramePkt(callId: callId, isSilence: false, keyGen: 7, audio: audio)
        try await svc.handlePacket(framePkt)

        XCTAssertTrue(frameReceived, "onFrameReceived must fire for inbound group voice frame")
        XCTAssertEqual(receivedAudio, audio)
        XCTAssertEqual(receivedKeyGen, 7, "key generation must be decoded correctly from wire format")
    }
}
