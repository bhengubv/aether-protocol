// SPDX-License-Identifier: MIT

import Foundation

// ─── Media frame models ───────────────────────────────────

/// A push-to-talk audio frame (``PacketType/voicePtt`` = 15 body).
public struct VoicePttFrame: Equatable, Sendable {
    public var callId: UUID
    public var sequence: UInt32
    public var timestampMs: Int64
    public var isSilence: Bool
    public var encodedPayload: Data

    public init(
        callId: UUID,
        sequence: UInt32 = 0,
        timestampMs: Int64 = 0,
        isSilence: Bool = false,
        encodedPayload: Data = Data()
    ) {
        self.callId = callId
        self.sequence = sequence
        self.timestampMs = timestampMs
        self.isSilence = isSilence
        self.encodedPayload = encodedPayload
    }
}

/// A screen-share video frame (``PacketType/screenShare`` = 32 body).
public struct ScreenShareFrame: Equatable, Sendable {
    public var callId: UUID
    public var sequence: UInt32
    public var timestampMs: Int64
    public var isKeyframe: Bool
    public var encodedPayload: Data

    public init(
        callId: UUID,
        sequence: UInt32 = 0,
        timestampMs: Int64 = 0,
        isKeyframe: Bool = false,
        encodedPayload: Data = Data()
    ) {
        self.callId = callId
        self.sequence = sequence
        self.timestampMs = timestampMs
        self.isKeyframe = isKeyframe
        self.encodedPayload = encodedPayload
    }
}

// ─── Binary codec ─────────────────────────────────────────

/// Binary codec for the VoicePtt(15) and ScreenShare(32) media frames. Both share the exact
/// 29-byte header used by the existing VoiceCall(16)/VideoFrame(31) frames, so a node can treat
/// them uniformly:
///
///   [0..15]  call_id       — 16 bytes, RFC-4122 BIG-ENDIAN (network order)
///   [16..19] sequence      — u32 LITTLE-ENDIAN
///   [20..27] timestamp_ms  — i64 LITTLE-ENDIAN
///   [28]     flag          — u8 (VoicePtt: is_silence; ScreenShare: is_keyframe)
///   [29..]   payload       — opaque encoded audio/video bytes
///
/// Byte-identity gate: `fixtures/media/vectors.json` (expected_hex). The call_id is written in
/// big-endian (network) order — `withUnsafeBytes(of: uuid.uuid)` yields exactly those bytes
/// because `UUID.uuid` is a 16-byte tuple already in RFC-4122 order, NOT the .NET mixed-endian
/// `Guid.ToByteArray()` layout. This mirrors the C# `MediaFrameCodec`.
public enum MediaFrameCodec {
    /// Shared header size in bytes.
    public static let headerLength = 29

    // MARK: - Serialize

    public static func serializeVoicePtt(_ f: VoicePttFrame) -> Data {
        serialize(callId: f.callId, sequence: f.sequence, timestampMs: f.timestampMs,
                  flag: f.isSilence, payload: f.encodedPayload)
    }

    public static func serializeScreenShare(_ f: ScreenShareFrame) -> Data {
        serialize(callId: f.callId, sequence: f.sequence, timestampMs: f.timestampMs,
                  flag: f.isKeyframe, payload: f.encodedPayload)
    }

    private static func serialize(callId: UUID, sequence: UInt32, timestampMs: Int64, flag: Bool, payload: Data) -> Data {
        var buf = Data(capacity: headerLength + payload.count)
        // [0..15] call_id — RFC-4122 big-endian (network order): uuid tuple is already in that order.
        withUnsafeBytes(of: callId.uuid) { buf.append(contentsOf: $0) }
        // [16..19] sequence — u32 LE.
        var seq = sequence.littleEndian
        withUnsafeBytes(of: &seq) { buf.append(contentsOf: $0) }
        // [20..27] timestamp_ms — i64 LE.
        var ts = timestampMs.littleEndian
        withUnsafeBytes(of: &ts) { buf.append(contentsOf: $0) }
        // [28] flag.
        buf.append(flag ? 1 : 0)
        // [29..] payload.
        buf.append(payload)
        return buf
    }

    // MARK: - Deserialize

    /// Parses a VoicePtt frame body. Returns `nil` if the buffer is shorter than the 29-byte header.
    public static func deserializeVoicePtt(_ data: Data) -> VoicePttFrame? {
        guard let (callId, seq, tsMs, flag, payload) = deserialize(data) else { return nil }
        return VoicePttFrame(callId: callId, sequence: seq, timestampMs: tsMs, isSilence: flag, encodedPayload: payload)
    }

    /// Parses a ScreenShare frame body. Returns `nil` if the buffer is shorter than the 29-byte header.
    public static func deserializeScreenShare(_ data: Data) -> ScreenShareFrame? {
        guard let (callId, seq, tsMs, flag, payload) = deserialize(data) else { return nil }
        return ScreenShareFrame(callId: callId, sequence: seq, timestampMs: tsMs, isKeyframe: flag, encodedPayload: payload)
    }

    private static func deserialize(_ data: Data) -> (UUID, UInt32, Int64, Bool, Data)? {
        guard data.count >= headerLength else { return nil }
        // Re-base to a zero-indexed contiguous buffer — `data` may arrive as a slice whose
        // startIndex is non-zero, and `subdata(in:)` uses absolute indices.
        let b = Data(data)
        let callId = UUID(uuid: b.subdata(in: 0..<16).withUnsafeBytes { $0.load(as: uuid_t.self) })
        let seq    = UInt32(littleEndian: b.subdata(in: 16..<20).withUnsafeBytes { $0.load(as: UInt32.self) })
        let tsMs   = Int64(littleEndian: b.subdata(in: 20..<28).withUnsafeBytes { $0.load(as: Int64.self) })
        let flag   = b[28] != 0
        let payload = b.subdata(in: headerLength..<b.count)
        return (callId, seq, tsMs, flag, payload)
    }
}
