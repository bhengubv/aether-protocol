// SPDX-License-Identifier: MIT
// NOTE: CI on Linux is the verification gate.

import Foundation

// ─── GroupVoiceCallService ────────────────────────────────

/// Multi-party mesh voice call service (up to ProtocolConstants.maxGroupVoiceMembers).
///
/// Wire format for GroupVoiceFrame payload:
///   [16] CallId  (UUID, RFC4122 big-endian)
///   [4]  Sequence (UInt32 little-endian)
///   [8]  TimestampMs (Int64 little-endian)
///   [1]  IsSilence (UInt8: 0 or 1)
///   [4]  KeyGeneration (UInt32 little-endian)
///   [N]  EncodedPayload
///
/// Signalling uses JSON (Codable, snake_case) in MeshPacket.payload.
public actor GroupVoiceCallService {
    private let sender: any MeshSender

    private var groupCalls: [UUID: GroupCallRecord] = [:]
    private var frameSequence: [UUID: UInt32] = [:]

    public var onInviteReceived: (@Sendable (UUID, String, [String]) -> Void)?
    public var onMemberJoined: (@Sendable (UUID, String) -> Void)?
    public var onMemberLeft: (@Sendable (UUID, String) -> Void)?
    /// (callId, senderUhid, audio, isSilence, keyGeneration, timestampMs)
    public var onFrameReceived: (@Sendable (UUID, String, Data, Bool, UInt32, Int64) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    // MARK: – Callbacks

    public func setOnInviteReceived(_ cb: (@Sendable (UUID, String, [String]) -> Void)?) {
        onInviteReceived = cb
    }
    public func setOnMemberJoined(_ cb: (@Sendable (UUID, String) -> Void)?) {
        onMemberJoined = cb
    }
    public func setOnMemberLeft(_ cb: (@Sendable (UUID, String) -> Void)?) {
        onMemberLeft = cb
    }
    public func setOnFrameReceived(_ cb: (@Sendable (UUID, String, Data, Bool, UInt32, Int64) -> Void)?) {
        onFrameReceived = cb
    }

    // MARK: – Session management

    /// Create a new group call and invite `toUhids`.
    public func invite(toUhids: [String], codecs: [String]) async throws -> UUID {
        let callId = UUID()
        var members = [sender.localUhid]
        for u in toUhids where !members.contains(u) { members.append(u) }
        groupCalls[callId] = GroupCallRecord(callId: callId, members: members, codecs: codecs)
        frameSequence[callId] = 0

        let wire = GroupInviteWire(call_id: callId, from_uhid: sender.localUhid, codecs: codecs, members: members)
        for uhid in toUhids {
            await sendSignaling(encodeJSON(wire), toUhid: uhid)
        }
        return callId
    }

    /// Accept an invite and broadcast join to all members.
    public func join(callId: UUID) async throws {
        guard var record = groupCalls[callId] else { return }
        if !record.members.contains(sender.localUhid) { record.members.append(sender.localUhid) }
        groupCalls[callId] = record
        frameSequence[callId] = 0

        let wire = GroupMemberWire(call_id: callId, uhid: sender.localUhid, signal_type: "group_join")
        for uhid in record.members where uhid != sender.localUhid {
            await sendSignaling(encodeJSON(wire), toUhid: uhid)
        }
        onMemberJoined?(callId, sender.localUhid)
    }

    /// Leave a group call gracefully.
    public func leave(callId: UUID) async throws {
        guard let record = groupCalls[callId] else { return }
        let wire = GroupMemberWire(call_id: callId, uhid: sender.localUhid, signal_type: "group_leave")
        for uhid in record.members where uhid != sender.localUhid {
            await sendSignaling(encodeJSON(wire), toUhid: uhid)
        }
        groupCalls.removeValue(forKey: callId)
        frameSequence.removeValue(forKey: callId)
        onMemberLeft?(callId, sender.localUhid)
    }

    /// Kick a member.
    public func kick(callId: UUID, uhid: String) async throws {
        guard var record = groupCalls[callId] else { return }
        record.members.removeAll { $0 == uhid }
        groupCalls[callId] = record

        let wire = GroupKickWire(call_id: callId, kicked_uhid: uhid, by_uhid: sender.localUhid)
        let payload = encodeJSON(wire)
        for member in record.members where member != sender.localUhid {
            await sendSignaling(payload, toUhid: member)
        }
        await sendSignaling(payload, toUhid: uhid)
        onMemberLeft?(callId, uhid)
    }

    // MARK: – Frame sending

    /// Send an encoded group audio frame.
    public func sendFrame(callId: UUID, encodedAudio: Data, isSilence: Bool, keyGeneration: UInt32 = 0) async throws {
        guard let record = groupCalls[callId] else { return }
        let seq = frameSequence[callId, default: 0]
        frameSequence[callId] = seq &+ 1
        let tsMs = Int64(Date().timeIntervalSince1970 * 1000)
        let frameData = encodeGroupVoiceFrame(callId: callId, sequence: seq, timestampMs: tsMs, isSilence: isSilence, keyGeneration: keyGeneration, audio: encodedAudio)

        for uhid in record.members where uhid != sender.localUhid {
            var pkt = MeshPacket(type: .voiceCall, sourceUhid: sender.localUhid, destinationUhid: uhid, priority: 64)
            pkt.payload = frameData
            _ = await sender.send(pkt, nextHopUhid: uhid)
        }
    }

    // MARK: – Inbound dispatch

    public func handlePacket(_ packet: MeshPacket) async throws {
        switch packet.type {
        case .voiceSignaling: handleGroupSignaling(packet)
        case .voiceCall:      handleGroupFrame(packet)
        default: break
        }
    }

    // MARK: – Private

    private func handleGroupSignaling(_ packet: MeshPacket) {
        if let invite = decodeJSON(GroupInviteWire.self, from: packet.payload) {
            var members = invite.members
            if !members.contains(sender.localUhid) { members.append(sender.localUhid) }
            groupCalls[invite.call_id] = GroupCallRecord(callId: invite.call_id, members: members, codecs: invite.codecs)
            onInviteReceived?(invite.call_id, packet.sourceUhid, invite.codecs)
            return
        }
        if let member = decodeJSON(GroupMemberWire.self, from: packet.payload) {
            switch member.signal_type {
            case "group_join":
                if var r = groupCalls[member.call_id] {
                    if !r.members.contains(member.uhid) { r.members.append(member.uhid) }
                    groupCalls[member.call_id] = r
                    onMemberJoined?(member.call_id, member.uhid)
                }
            case "group_leave":
                if var r = groupCalls[member.call_id] {
                    r.members.removeAll { $0 == member.uhid }
                    groupCalls[member.call_id] = r
                    onMemberLeft?(member.call_id, member.uhid)
                }
            default: break
            }
            return
        }
        if let kick = decodeJSON(GroupKickWire.self, from: packet.payload) {
            if kick.kicked_uhid == sender.localUhid {
                groupCalls.removeValue(forKey: kick.call_id)
                frameSequence.removeValue(forKey: kick.call_id)
                onMemberLeft?(kick.call_id, sender.localUhid)
            } else if var r = groupCalls[kick.call_id] {
                r.members.removeAll { $0 == kick.kicked_uhid }
                groupCalls[kick.call_id] = r
                onMemberLeft?(kick.call_id, kick.kicked_uhid)
            }
        }
    }

    private func handleGroupFrame(_ packet: MeshPacket) {
        guard let (callId, _, tsMs, isSilence, keyGen, audio) = decodeGroupVoiceFrame(packet.payload) else { return }
        onFrameReceived?(callId, packet.sourceUhid, audio, isSilence, keyGen, tsMs)
    }

    private func sendSignaling(_ payload: Data, toUhid: String) async {
        var pkt = MeshPacket(type: .voiceSignaling, sourceUhid: sender.localUhid, destinationUhid: toUhid, priority: 32)
        pkt.payload = payload
        _ = await sender.send(pkt, nextHopUhid: toUhid)
    }
}

// ─── Internal model ───────────────────────────────────────

private struct GroupCallRecord: Sendable {
    var callId: UUID
    var members: [String]
    var codecs: [String]
}

// ─── JSON wire types ──────────────────────────────────────

private struct GroupInviteWire: Codable {
    @LowercaseUUIDCoding var call_id: UUID
    let from_uhid: String
    let codecs: [String]
    let members: [String]
    let signal_type: String = "group_invite"
    private enum CodingKeys: String, CodingKey {
        case call_id, from_uhid, codecs, members, signal_type
    }
}

private struct GroupMemberWire: Codable {
    @LowercaseUUIDCoding var call_id: UUID
    let uhid: String
    let signal_type: String   // "group_join" | "group_leave"
}

private struct GroupKickWire: Codable {
    @LowercaseUUIDCoding var call_id: UUID
    let kicked_uhid: String
    let by_uhid: String
    let signal_type: String = "group_kick"
    private enum CodingKeys: String, CodingKey {
        case call_id, kicked_uhid, by_uhid, signal_type
    }
}

// ─── Binary GroupVoiceFrame helpers ──────────────────────
// Layout: [16 CallId BE][4 Seq LE][8 TsMs LE][1 IsSilence][4 KeyGen LE][N Audio]

private func encodeGroupVoiceFrame(callId: UUID, sequence: UInt32, timestampMs: Int64, isSilence: Bool, keyGeneration: UInt32, audio: Data) -> Data {
    var buf = Data(capacity: 33 + audio.count)
    var uuidBytes = callId.uuid
    withUnsafeBytes(of: &uuidBytes) { buf.append(contentsOf: $0) }
    var seq = sequence.littleEndian
    withUnsafeBytes(of: &seq) { buf.append(contentsOf: $0) }
    var ts = timestampMs.littleEndian
    withUnsafeBytes(of: &ts) { buf.append(contentsOf: $0) }
    buf.append(isSilence ? 1 : 0)
    var kg = keyGeneration.littleEndian
    withUnsafeBytes(of: &kg) { buf.append(contentsOf: $0) }
    buf.append(audio)
    return buf
}

private func decodeGroupVoiceFrame(_ data: Data) -> (UUID, UInt32, Int64, Bool, UInt32, Data)? {
    guard data.count >= 33 else { return nil }
    let callId  = UUID(uuid: data.subdata(in: 0..<16).withUnsafeBytes { $0.load(as: uuid_t.self) })
    let seq     = UInt32(littleEndian: data.subdata(in: 16..<20).withUnsafeBytes { $0.load(as: UInt32.self) })
    let tsMs    = Int64(littleEndian: data.subdata(in: 20..<28).withUnsafeBytes { $0.load(as: Int64.self) })
    let isSilence = data[28] != 0
    let keyGen  = UInt32(littleEndian: data.subdata(in: 29..<33).withUnsafeBytes { $0.load(as: UInt32.self) })
    return (callId, seq, tsMs, isSilence, keyGen, data.subdata(in: 33..<data.count))
}

private func encodeJSON<T: Encodable>(_ value: T) -> Data {
    (try? JSONEncoder().encode(value)) ?? Data()
}

private func decodeJSON<T: Decodable>(_ type: T.Type, from data: Data) -> T? {
    try? JSONDecoder().decode(type, from: data)
}
