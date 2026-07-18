// SPDX-License-Identifier: MIT

import Foundation

/// A µTP packet type (BEP-29).
public enum UtpPacketType: UInt8 {
    case data = 0
    case fin = 1
    case state = 2
    case reset = 3
    case syn = 4
}

/// The µTP protocol version this SDK speaks.
public let utpVersion: UInt8 = 1

/// The fixed µTP header length.
public let utpHeaderSize = 20

/// A µTP packet (BEP-29, version 1). The 20-byte header is
/// type|version(1) · extension(1) · connection_id(2) · timestamp_us(4) ·
/// timestamp_diff_us(4) · wnd_size(4) · seq_nr(2) · ack_nr(2), all big-endian.
public struct UtpPacket {
    public var type: UtpPacketType
    public var connectionID: UInt16
    public var timestampMicros: UInt32
    public var timestampDiff: UInt32
    public var windowSize: UInt32
    public var seqNr: UInt16
    public var ackNr: UInt16
    public var payload: [UInt8]

    public init(type: UtpPacketType,
                connectionID: UInt16 = 0,
                timestampMicros: UInt32 = 0,
                timestampDiff: UInt32 = 0,
                windowSize: UInt32 = 0,
                seqNr: UInt16 = 0,
                ackNr: UInt16 = 0,
                payload: [UInt8] = []) {
        self.type = type
        self.connectionID = connectionID
        self.timestampMicros = timestampMicros
        self.timestampDiff = timestampDiff
        self.windowSize = windowSize
        self.seqNr = seqNr
        self.ackNr = ackNr
        self.payload = payload
    }

    /// Serializes the packet (no extensions).
    public func toBytes() -> [UInt8] {
        var buf = [UInt8](repeating: 0, count: utpHeaderSize + payload.count)
        buf[0] = (type.rawValue << 4) | utpVersion
        buf[1] = 0  // no extensions
        putUInt16BE(connectionID, into: &buf, at: 2)
        putUInt32BE(timestampMicros, into: &buf, at: 4)
        putUInt32BE(timestampDiff, into: &buf, at: 8)
        putUInt32BE(windowSize, into: &buf, at: 12)
        putUInt16BE(seqNr, into: &buf, at: 16)
        putUInt16BE(ackNr, into: &buf, at: 18)
        for i in 0..<payload.count { buf[utpHeaderSize + i] = payload[i] }
        return buf
    }
}

public enum UtpError: Error, Equatable {
    case invalid(String)
}

/// Parses a µTP packet, walking any extension chain to find the payload.
public func parseUtpPacket(_ data: [UInt8]) throws -> UtpPacket {
    if data.count < utpHeaderSize {
        throw UtpError.invalid("µTP packet is \(data.count) bytes, shorter than the \(utpHeaderSize)-byte header")
    }
    let version = data[0] & 0x0F
    if version != utpVersion {
        throw UtpError.invalid("unsupported µTP version \(version)")
    }
    guard let type = UtpPacketType(rawValue: data[0] >> 4) else {
        throw UtpError.invalid("unknown µTP packet type \(data[0] >> 4)")
    }

    // Walk the extension chain (each: next_ext(1) len(1) data(len)).
    var offset = utpHeaderSize
    var nextExt = Int(data[1])
    while nextExt != 0 {
        if offset + 2 > data.count {
            throw UtpError.invalid("truncated µTP extension header")
        }
        let thisNext = Int(data[offset])
        let extLen = Int(data[offset + 1])
        offset += 2 + extLen
        if offset > data.count {
            throw UtpError.invalid("truncated µTP extension data")
        }
        nextExt = thisNext
    }

    return UtpPacket(
        type: type,
        connectionID: readUInt16BE(data, at: 2),
        timestampMicros: readUInt32BE(data, at: 4),
        timestampDiff: readUInt32BE(data, at: 8),
        windowSize: readUInt32BE(data, at: 12),
        seqNr: readUInt16BE(data, at: 16),
        ackNr: readUInt16BE(data, at: 18),
        payload: Array(data[offset...])
    )
}
