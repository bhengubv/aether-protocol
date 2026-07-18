// SPDX-License-Identifier: MIT

import Foundation

let protocolString = "BitTorrent protocol"

// ── big-endian byte helpers ──────────────────────────────────────────────────

@inline(__always)
func putUInt32BE(_ v: UInt32, into buf: inout [UInt8], at offset: Int) {
    buf[offset]     = UInt8((v >> 24) & 0xFF)
    buf[offset + 1] = UInt8((v >> 16) & 0xFF)
    buf[offset + 2] = UInt8((v >> 8) & 0xFF)
    buf[offset + 3] = UInt8(v & 0xFF)
}

@inline(__always)
func putUInt16BE(_ v: UInt16, into buf: inout [UInt8], at offset: Int) {
    buf[offset]     = UInt8((v >> 8) & 0xFF)
    buf[offset + 1] = UInt8(v & 0xFF)
}

@inline(__always)
func readUInt32BE(_ buf: [UInt8], at offset: Int) -> UInt32 {
    (UInt32(buf[offset]) << 24) | (UInt32(buf[offset + 1]) << 16)
        | (UInt32(buf[offset + 2]) << 8) | UInt32(buf[offset + 3])
}

@inline(__always)
func readUInt16BE(_ buf: [UInt8], at offset: Int) -> UInt16 {
    (UInt16(buf[offset]) << 8) | UInt16(buf[offset + 1])
}

// ── handshake ────────────────────────────────────────────────────────────────

/// The 68-byte BitTorrent peer-wire handshake (BEP-3):
/// pstrlen(1)=19 · "BitTorrent protocol"(19) · reserved(8) · info_hash(20) · peer_id(20).
public struct Handshake {
    public var reserved: [UInt8]   // 8 bytes
    public var infoHash: [UInt8]   // 20 bytes
    public var peerID: [UInt8]     // 20 bytes

    public init(reserved: [UInt8] = [UInt8](repeating: 0, count: 8),
                infoHash: [UInt8] = [UInt8](repeating: 0, count: 20),
                peerID: [UInt8] = [UInt8](repeating: 0, count: 20)) {
        self.reserved = reserved
        self.infoHash = infoHash
        self.peerID = peerID
    }

    /// Advertises the extension protocol (BEP-10) and DHT (BEP-5).
    public static func defaultReserved() -> [UInt8] {
        var r = [UInt8](repeating: 0, count: 8)
        r[5] |= 0x10  // extension protocol
        r[7] |= 0x01  // DHT
        return r
    }

    /// Serializes the 68-byte handshake.
    public func toBytes() -> [UInt8] {
        var buf = [UInt8](repeating: 0, count: 68)
        buf[0] = 19
        let proto = Array(protocolString.utf8)
        for i in 0..<19 { buf[1 + i] = proto[i] }
        for i in 0..<8 { buf[20 + i] = reserved[i] }
        for i in 0..<20 { buf[28 + i] = infoHash[i] }
        for i in 0..<20 { buf[48 + i] = peerID[i] }
        return buf
    }

    /// Reports whether the reserved bits advertise BEP-10.
    public var supportsExtended: Bool { reserved[5] & 0x10 != 0 }

    /// Reports whether the reserved bits advertise BEP-5.
    public var supportsDht: Bool { reserved[7] & 0x01 != 0 }
}

public enum PeerWireError: Error, Equatable {
    case invalid(String)
}

/// Parses a 68-byte handshake.
public func parseHandshake(_ data: [UInt8]) throws -> Handshake {
    if data.count < 68 {
        throw PeerWireError.invalid("handshake is \(data.count) bytes, need 68")
    }
    if data[0] != 19 {
        throw PeerWireError.invalid("handshake pstrlen is \(data[0]), want 19")
    }
    if Array(data[1..<20]) != Array(protocolString.utf8) {
        throw PeerWireError.invalid("handshake protocol string mismatch")
    }
    return Handshake(
        reserved: Array(data[20..<28]),
        infoHash: Array(data[28..<48]),
        peerID: Array(data[48..<68])
    )
}

// ── messages ─────────────────────────────────────────────────────────────────

/// A BEP-3 peer-wire message id (plus 20 = BEP-10 extended).
public enum MessageType: UInt8 {
    case choke = 0
    case unchoke = 1
    case interested = 2
    case notInterested = 3
    case have = 4
    case bitfield = 5
    case request = 6
    case piece = 7
    case cancel = 8
    case port = 9
    case extended = 20
}

/// A peer-wire message. A keep-alive has `hasID == false` (zero-length frame).
public struct PeerMessage {
    public var hasID: Bool
    public var id: MessageType
    public var payload: [UInt8]

    public init(hasID: Bool = false, id: MessageType = .choke, payload: [UInt8] = []) {
        self.hasID = hasID
        self.id = id
        self.payload = payload
    }

    /// Serializes the message with its 4-byte big-endian length prefix.
    public func toBytes() -> [UInt8] {
        if !hasID {
            return [0, 0, 0, 0]  // keep-alive
        }
        let length = 1 + payload.count
        var buf = [UInt8](repeating: 0, count: 4 + length)
        putUInt32BE(UInt32(length), into: &buf, at: 0)
        buf[4] = id.rawValue
        for i in 0..<payload.count { buf[5 + i] = payload[i] }
        return buf
    }

    /// Decodes a Have payload.
    public func havePieceIndex() throws -> UInt32 {
        if id != .have || payload.count != 4 {
            throw PeerWireError.invalid("not a valid have message")
        }
        return readUInt32BE(payload, at: 0)
    }

    /// Decodes a Request/Cancel payload (index, begin, length).
    public func blockRef() throws -> (index: UInt32, begin: UInt32, length: UInt32) {
        if (id != .request && id != .cancel) || payload.count != 12 {
            throw PeerWireError.invalid("not a valid request/cancel message")
        }
        return (readUInt32BE(payload, at: 0), readUInt32BE(payload, at: 4), readUInt32BE(payload, at: 8))
    }

    /// Decodes a Piece payload (index, begin, block).
    public func pieceBlock() throws -> (index: UInt32, begin: UInt32, block: [UInt8]) {
        if id != .piece || payload.count < 8 {
            throw PeerWireError.invalid("not a valid piece message")
        }
        return (readUInt32BE(payload, at: 0), readUInt32BE(payload, at: 4), Array(payload[8...]))
    }

    /// Decodes a Port payload.
    public func portValue() throws -> UInt16 {
        if id != .port || payload.count != 2 {
            throw PeerWireError.invalid("not a valid port message")
        }
        return readUInt16BE(payload, at: 0)
    }
}

// Message factories (mirror the Go free functions).

public func keepAlive() -> PeerMessage { PeerMessage() }

public func newMessage(_ id: MessageType, _ payload: [UInt8]) -> PeerMessage {
    PeerMessage(hasID: true, id: id, payload: payload)
}

public func choke() -> PeerMessage { newMessage(.choke, []) }
public func unchoke() -> PeerMessage { newMessage(.unchoke, []) }
public func interested() -> PeerMessage { newMessage(.interested, []) }
public func notInterested() -> PeerMessage { newMessage(.notInterested, []) }

public func have(_ pieceIndex: UInt32) -> PeerMessage {
    var p = [UInt8](repeating: 0, count: 4)
    putUInt32BE(pieceIndex, into: &p, at: 0)
    return newMessage(.have, p)
}

public func bitfieldMsg(_ bits: [UInt8]) -> PeerMessage { newMessage(.bitfield, bits) }

public func request(_ index: UInt32, _ begin: UInt32, _ length: UInt32) -> PeerMessage {
    var p = [UInt8](repeating: 0, count: 12)
    putUInt32BE(index, into: &p, at: 0)
    putUInt32BE(begin, into: &p, at: 4)
    putUInt32BE(length, into: &p, at: 8)
    return newMessage(.request, p)
}

public func cancel(_ index: UInt32, _ begin: UInt32, _ length: UInt32) -> PeerMessage {
    var m = request(index, begin, length)
    m.id = .cancel
    return m
}

public func piece(_ index: UInt32, _ begin: UInt32, _ block: [UInt8]) -> PeerMessage {
    var p = [UInt8](repeating: 0, count: 8 + block.count)
    putUInt32BE(index, into: &p, at: 0)
    putUInt32BE(begin, into: &p, at: 4)
    for i in 0..<block.count { p[8 + i] = block[i] }
    return newMessage(.piece, p)
}

public func port(_ portValue: UInt16) -> PeerMessage {
    var p = [UInt8](repeating: 0, count: 2)
    putUInt16BE(portValue, into: &p, at: 0)
    return newMessage(.port, p)
}

public func extended(_ subID: UInt8, _ body: [UInt8]) -> PeerMessage {
    var p = [UInt8](repeating: 0, count: 1 + body.count)
    p[0] = subID
    for i in 0..<body.count { p[1 + i] = body[i] }
    return newMessage(.extended, p)
}

/// Parses a message body (id + payload, no length prefix). Empty = keep-alive.
public func parseBody(_ body: [UInt8]) -> PeerMessage {
    if body.isEmpty {
        return keepAlive()
    }
    let id = MessageType(rawValue: body[0]) ?? .choke
    return newMessage(id, Array(body[1...]))
}

/// Parses a full length-prefixed frame, returning the message and bytes consumed.
public func parseFrame(_ data: [UInt8]) throws -> (PeerMessage, Int) {
    if data.count < 4 {
        throw PeerWireError.invalid("frame shorter than 4-byte length prefix")
    }
    let length = Int(readUInt32BE(data, at: 0))
    if length + 4 > data.count {
        throw PeerWireError.invalid("frame length \(length) exceeds available \(data.count - 4)")
    }
    let msg = parseBody(Array(data[4..<(4 + length)]))
    return (msg, 4 + length)
}

// ── Bitfield (MSB-first: piece 0 is 0x80 of byte 0) ──────────────────────────

public final class Bitfield {
    private var bits: [UInt8]
    public let count: Int

    /// Allocates a cleared bitfield for `pieceCount` pieces.
    public init(pieceCount: Int) {
        self.bits = [UInt8](repeating: 0, count: (pieceCount + 7) / 8)
        self.count = pieceCount
    }

    /// Wraps received bytes for `pieceCount` pieces.
    public init(fromBytes data: [UInt8], pieceCount: Int) {
        let need = (pieceCount + 7) / 8
        var b = [UInt8](repeating: 0, count: need)
        for i in 0..<min(need, data.count) { b[i] = data[i] }
        self.bits = b
        self.count = pieceCount
    }

    public func get(_ i: Int) -> Bool {
        if i < 0 || i >= count { return false }
        return bits[i >> 3] & (0x80 >> UInt8(i & 7)) != 0
    }

    public func set(_ i: Int) {
        if i < 0 || i >= count { return }
        bits[i >> 3] |= 0x80 >> UInt8(i & 7)
    }

    public func popCount() -> Int {
        var n = 0
        for i in 0..<count where get(i) { n += 1 }
        return n
    }

    public func hasAll() -> Bool { popCount() == count }

    public func toBytes() -> [UInt8] { bits }
}
