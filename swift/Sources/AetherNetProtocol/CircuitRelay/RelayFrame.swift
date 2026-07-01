// SPDX-License-Identifier: MIT

import Foundation

/// Circuit-relay-v2 verb (message type).
public enum RelayMessageType: UInt8 {
    case reserve = 1
    case reserveResponse = 2
    case connect = 3
    case stop = 4
    case stopResponse = 5
    case connectResponse = 6
    case data = 7
}

/// Circuit-relay-v2 response status code.
public enum RelayStatus: UInt8 {
    case ok = 0
    case reservationRefused = 1
    case noReservation = 2
    case resourceLimitExceeded = 3
    case permissionDenied = 4
    case connectionFailed = 5
    case malformedMessage = 6
}

/// A single native circuit-relay-v2 wire frame (one fixed layout carries every verb).
public struct RelayFrame {
    public var type: RelayMessageType
    public var status: RelayStatus
    public var sourceUhid: String
    public var destinationUhid: String
    public var relayUhid: String
    public var connectionId: UUID
    public var reservationExpiresAtMs: Int64
    public var limitDurationSeconds: Int32
    public var limitDataBytes: Int64
    public var payload: Data

    public init(type: RelayMessageType,
                status: RelayStatus = .ok,
                sourceUhid: String = "",
                destinationUhid: String = "",
                relayUhid: String = "",
                connectionId: UUID = UUID(uuid: (0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0)),
                reservationExpiresAtMs: Int64 = 0,
                limitDurationSeconds: Int32 = 0,
                limitDataBytes: Int64 = 0,
                payload: Data = Data()) {
        self.type = type
        self.status = status
        self.sourceUhid = sourceUhid
        self.destinationUhid = destinationUhid
        self.relayUhid = relayUhid
        self.connectionId = connectionId
        self.reservationExpiresAtMs = reservationExpiresAtMs
        self.limitDurationSeconds = limitDurationSeconds
        self.limitDataBytes = limitDataBytes
        self.payload = payload
    }
}

/// Canonical binary circuit-relay-v2 frame serializer (wire format v1). Byte-identical
/// across all eight AetherNet SDKs; the Go encoder (go/cmd/circuitrelayfixturegen) is the
/// oracle and fixtures/circuit-relay/expected/*.bin pins the bytes. Conventions mirror
/// `DtnEnvelope`: little-endian integers; the 16-byte connection id is the raw RFC-4122
/// big-endian UUID; strings are uint16-LE length-prefixed UTF-8; payload is int32-LE
/// length-prefixed and last.
public enum RelayFrameSerializer {
    static let version: UInt8 = 0x01
    static let maxPayload = 16 * 1024 * 1024

    public static func serialize(_ f: RelayFrame) -> Data {
        var out = Data()
        out.append(version)
        out.append(f.type.rawValue)
        out.append(f.status.rawValue)
        appendStr(&out, f.sourceUhid)
        appendStr(&out, f.destinationUhid)
        appendStr(&out, f.relayUhid)
        out.append(uuidBytes(f.connectionId))
        appendI64(&out, f.reservationExpiresAtMs)
        appendI32(&out, f.limitDurationSeconds)
        appendI64(&out, f.limitDataBytes)
        appendBytes32(&out, f.payload)
        return out
    }

    public static func deserialize(_ data: Data) -> RelayFrame? {
        var r = Reader(data)
        guard r.version(),
              let typeRaw = r.u8(), let type = RelayMessageType(rawValue: typeRaw),
              let statusRaw = r.u8(), let status = RelayStatus(rawValue: statusRaw),
              let src = r.str(),
              let dst = r.str(),
              let relay = r.str(),
              let connId = r.uuid(),
              let resExp = r.i64(),
              let limDur = r.i32(),
              let limData = r.i64(),
              let payload = r.bytes32()
        else { return nil }
        return RelayFrame(
            type: type, status: status,
            sourceUhid: src, destinationUhid: dst, relayUhid: relay,
            connectionId: connId,
            reservationExpiresAtMs: resExp,
            limitDurationSeconds: limDur,
            limitDataBytes: limData,
            payload: payload)
    }

    // MARK: - primitives (mirror DtnEnvelope)

    private static func uuidBytes(_ id: UUID) -> Data {
        var u = id.uuid
        return withUnsafeBytes(of: &u) { Data($0) }
    }

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

    private static func appendStr(_ out: inout Data, _ s: String) {
        let bytes = Array(s.utf8)
        let n = min(bytes.count, 0xFFFF)
        appendU16(&out, n)
        out.append(contentsOf: bytes[0..<n])
    }

    private static func appendBytes32(_ out: inout Data, _ b: Data) {
        appendI32(&out, Int32(truncatingIfNeeded: b.count))
        out.append(b)
    }

    private struct Reader {
        let d: [UInt8]
        var o = 0

        init(_ data: Data) { d = Array(data) }

        mutating func version() -> Bool {
            guard let v = u8() else { return false }
            return v == RelayFrameSerializer.version
        }

        mutating func u8() -> UInt8? {
            guard o + 1 <= d.count else { return nil }
            defer { o += 1 }
            return d[o]
        }

        mutating func uuid() -> UUID? {
            guard o + 16 <= d.count else { return nil }
            let t: uuid_t = (
                d[o], d[o + 1], d[o + 2], d[o + 3], d[o + 4], d[o + 5], d[o + 6], d[o + 7],
                d[o + 8], d[o + 9], d[o + 10], d[o + 11], d[o + 12], d[o + 13], d[o + 14], d[o + 15]
            )
            o += 16
            return UUID(uuid: t)
        }

        mutating func i32() -> Int32? {
            guard o + 4 <= d.count else { return nil }
            let u = UInt32(d[o]) | (UInt32(d[o + 1]) << 8) | (UInt32(d[o + 2]) << 16) | (UInt32(d[o + 3]) << 24)
            o += 4
            return Int32(bitPattern: u)
        }

        mutating func i64() -> Int64? {
            guard o + 8 <= d.count else { return nil }
            var u: UInt64 = 0
            for i in 0..<8 { u |= UInt64(d[o + i]) << (8 * i) }
            o += 8
            return Int64(bitPattern: u)
        }

        mutating func u16() -> Int? {
            guard o + 2 <= d.count else { return nil }
            let v = Int(d[o]) | (Int(d[o + 1]) << 8)
            o += 2
            return v
        }

        mutating func str() -> String? {
            guard let n = u16(), o + n <= d.count else { return nil }
            let s = String(decoding: d[o..<o + n], as: UTF8.self)
            o += n
            return s
        }

        mutating func bytes32() -> Data? {
            guard let n = i32() else { return nil }
            let len = Int(n)
            guard n >= 0, len <= RelayFrameSerializer.maxPayload, o + len <= d.count else { return nil }
            let slice = Data(d[o..<o + len])
            o += len
            return slice
        }
    }
}
