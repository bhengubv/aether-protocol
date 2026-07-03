// SPDX-License-Identifier: MIT

import Foundation

/// Errors thrown while parsing a ``SyncRecord`` from its wire bytes.
public enum SyncRecordError: Error, Equatable {
    case tooShort
    case unsupportedVersion(UInt8)
    case unknownOp(UInt8)
    case truncatedString
    case truncatedPayloadLength
    case invalidPayloadLength
    case deviceIdTooLong
    case itemIdTooLong
}

/// Binary wire format for a ``SyncRecord`` — the unit a device gossips to a
/// user's other devices. Little-endian integers, RFC-4122 big-endian record id,
/// u16-length-prefixed UTF-8 strings, i32-length-prefixed payload — identical
/// bytes across every AetherNet SDK (verified against `fixtures/sync/vectors.json`).
///
/// Layout: version(u8=1) · record_id(16, big-endian) · op(u8) · logical_clock(i64 LE)
/// · created_at_ms(i64 LE) · device_id(u16 len + utf8) · item_id(u16 len + utf8)
/// · encrypted_payload(i32 len + bytes).
///
/// Mirrors the C# `SyncRecordSerializer` (`src/AetherNet.Security/Sync/`).
public enum SyncRecordSerializer {
    /// Wire format version; readers reject any other value.
    public static let formatVersion: UInt8 = 0x01

    // MARK: - Serialize / Deserialize

    /// Serializes a record to its canonical bytes.
    public static func serialize(_ record: SyncRecord) throws -> Data {
        let device = Array(record.deviceId.utf8)
        let item = Array(record.itemId.utf8)
        let payload = record.encryptedPayload
        if device.count > 0xFFFF { throw SyncRecordError.deviceIdTooLong }
        if item.count > 0xFFFF { throw SyncRecordError.itemIdTooLong }

        var out = Data()
        out.reserveCapacity(1 + 16 + 1 + 8 + 8 + 2 + device.count + 2 + item.count + 4 + payload.count)

        out.append(formatVersion)
        out.append(contentsOf: record.recordId)          // 16 bytes, big-endian
        out.append(record.op.rawValue)
        appendI64(&out, record.logicalClock)
        appendI64(&out, record.createdAtMs)
        appendString(&out, device)
        appendString(&out, item)
        appendI32(&out, Int32(truncatingIfNeeded: payload.count))
        out.append(payload)

        return out
    }

    /// Parses canonical bytes back into a record, validating framing.
    public static func deserialize(_ data: Data) throws -> SyncRecord {
        let d = Array(data)
        var o = 0

        // Minimum: version + record_id + op + 2×i64 + 2×(u16 len) + i32 payload len.
        if d.count < 1 + 16 + 1 + 8 + 8 + 2 + 2 + 4 { throw SyncRecordError.tooShort }
        if d[o] != formatVersion { throw SyncRecordError.unsupportedVersion(d[o]) }
        o += 1

        let recordId = Array(d[o..<o + 16]); o += 16
        let opByte = d[o]; o += 1
        guard opByte <= SyncOp.read.rawValue, let op = SyncOp(rawValue: opByte) else {
            throw SyncRecordError.unknownOp(opByte)
        }
        let logicalClock = readI64(d, &o)
        let createdAtMs = readI64(d, &o)
        let deviceId = try readString(d, &o)
        let itemId = try readString(d, &o)

        if o + 4 > d.count { throw SyncRecordError.truncatedPayloadLength }
        let payloadLen = Int(readI32(d, &o))
        if payloadLen < 0 || o + payloadLen > d.count { throw SyncRecordError.invalidPayloadLength }
        let payload = Data(d[o..<o + payloadLen]); o += payloadLen

        return SyncRecord(
            recordId: recordId,
            deviceId: deviceId,
            op: op,
            itemId: itemId,
            logicalClock: logicalClock,
            createdAtMs: createdAtMs,
            encryptedPayload: payload)
    }

    // MARK: - record_id <-> UUID string (16 raw big-endian bytes)

    /// The 16 raw big-endian (RFC-4122) bytes of a dashed UUID string, or `nil`
    /// if it is not a valid UUID. Swift's `UUID.uuid` tuple is already stored in
    /// RFC-4122 big-endian order, so these bytes match the fixture hex directly
    /// (same convention as the DTN envelope's bundle id).
    public static func recordIdBytes(fromUuidString s: String) -> [UInt8]? {
        guard let u = UUID(uuidString: s) else { return nil }
        var t = u.uuid
        return withUnsafeBytes(of: &t) { Array($0) }
    }

    /// The lower-case dashed UUID string for 16 raw big-endian bytes.
    public static func uuidString(fromRecordIdBytes b: [UInt8]) -> String {
        precondition(b.count == 16, "record id must be 16 bytes")
        let t: uuid_t = (
            b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
            b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]
        )
        return UUID(uuid: t).uuidString.lowercased()
    }

    // MARK: - primitives (little-endian, mirroring DtnEnvelope)

    private static func appendI32(_ out: inout Data, _ v: Int32) {
        let u = UInt32(bitPattern: v)
        out.append(UInt8(u & 0xff))
        out.append(UInt8((u >> 8) & 0xff))
        out.append(UInt8((u >> 16) & 0xff))
        out.append(UInt8((u >> 24) & 0xff))
    }

    private static func appendI64(_ out: inout Data, _ v: Int64) {
        let u = UInt64(bitPattern: v)
        for i in 0..<8 { out.append(UInt8((u >> (8 * i)) & 0xff)) }
    }

    private static func appendU16(_ out: inout Data, _ v: Int) {
        out.append(UInt8(v & 0xff))
        out.append(UInt8((v >> 8) & 0xff))
    }

    private static func appendString(_ out: inout Data, _ utf8: [UInt8]) {
        appendU16(&out, utf8.count)
        out.append(contentsOf: utf8)
    }

    private static func readI32(_ d: [UInt8], _ o: inout Int) -> Int32 {
        let u = UInt32(d[o]) | (UInt32(d[o + 1]) << 8) | (UInt32(d[o + 2]) << 16) | (UInt32(d[o + 3]) << 24)
        o += 4
        return Int32(bitPattern: u)
    }

    private static func readI64(_ d: [UInt8], _ o: inout Int) -> Int64 {
        var u: UInt64 = 0
        for i in 0..<8 { u |= UInt64(d[o + i]) << (8 * i) }
        o += 8
        return Int64(bitPattern: u)
    }

    private static func readU16(_ d: [UInt8], _ o: inout Int) -> Int {
        let v = Int(d[o]) | (Int(d[o + 1]) << 8)
        o += 2
        return v
    }

    private static func readString(_ d: [UInt8], _ o: inout Int) throws -> String {
        if o + 2 > d.count { throw SyncRecordError.truncatedString }
        let len = readU16(d, &o)
        if o + len > d.count { throw SyncRecordError.truncatedString }
        let s = String(decoding: d[o..<o + len], as: UTF8.self)
        o += len
        return s
    }
}
