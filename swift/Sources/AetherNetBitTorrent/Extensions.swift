// SPDX-License-Identifier: MIT

import Foundation

// ── BEP-10 extension protocol ────────────────────────────────────────────────

/// The peer-wire message id for extended messages (BEP-10).
public let extendedMessageID: UInt8 = 20

/// The extended sub-message id of the handshake.
public let extensionHandshakeID: UInt8 = 0

/// Builds an extended message payload: [subID][body]. This is the payload of a
/// peer-wire Extended (id 20) message.
public func wrapExtended(_ subID: UInt8, _ body: [UInt8]) -> [UInt8] {
    var out = [UInt8](repeating: 0, count: 1 + body.count)
    out[0] = subID
    for i in 0..<body.count { out[1 + i] = body[i] }
    return out
}

/// Splits an extended payload into its sub-message id and body.
public func splitExtended(_ payload: [UInt8]) throws -> (UInt8, [UInt8]) {
    if payload.isEmpty {
        throw PeerWireError.invalid("empty extended payload")
    }
    return (payload[0], Array(payload[1...]))
}

/// Builds a BEP-10 handshake advertising supported extensions (name → local
/// sub-message id) and optionally the metadata size.
public func buildExtensionHandshake(_ supported: [String: Int], metadataSize: Int) -> [UInt8] {
    let m = BDict()
    for (name, id) in supported {
        try! m.add(name, .int(Int64(id)))  // Dictionary keys are unique → cannot throw
    }
    let d = BDict()
    try! d.add("m", .dict(m))
    if metadataSize > 0 {
        try! d.add("metadata_size", .int(Int64(metadataSize)))
    }
    return wrapExtended(extensionHandshakeID, bencodeEncode(.dict(d)))
}

/// A parsed BEP-10 handshake.
public struct ExtensionHandshake {
    public var supported: [String: Int]
    public var metadataSize: Int

    /// The peer's ut_metadata sub-message id, or 0 if unsupported.
    public var metadataMessageID: Int { supported["ut_metadata"] ?? 0 }

    /// The peer's ut_pex sub-message id, or 0 if unsupported.
    public var pexMessageID: Int { supported["ut_pex"] ?? 0 }
}

/// Parses a BEP-10 handshake body (the bencode dict after the sub-id).
public func parseExtensionHandshake(_ body: [UInt8]) throws -> ExtensionHandshake {
    var h = ExtensionHandshake(supported: [:], metadataSize: 0)
    let v = try bencodeDecode(body)
    let d = try v.dictValue()
    if let mVal = d.get("m"), let md = try? mVal.dictValue() {
        for name in md.keys {
            if let idVal = md.get(name), let id = try? idVal.intValue() {
                h.supported[name] = Int(id)
            }
        }
    }
    if let sizeVal = d.get("metadata_size"), let n = try? sizeVal.intValue() {
        h.metadataSize = Int(n)
    }
    return h
}

// ── BEP-9 ut_metadata ────────────────────────────────────────────────────────

/// A ut_metadata message type.
public enum MetadataMessageType: Int {
    case request = 0
    case data = 1
    case reject = 2
}

/// The ut_metadata piece size (16 KiB).
public let metadataPieceSize = 16384

/// Builds a ut_metadata request for a piece.
public func buildMetadataRequest(_ piece: Int) -> [UInt8] {
    let d = BDict()
    try! d.add("msg_type", .int(Int64(MetadataMessageType.request.rawValue)))
    try! d.add("piece", .int(Int64(piece)))
    return bencodeEncode(.dict(d))
}

/// Builds a ut_metadata data message (bencode header + raw piece bytes).
public func buildMetadataData(_ piece: Int, _ totalSize: Int, _ data: [UInt8]) -> [UInt8] {
    let d = BDict()
    try! d.add("msg_type", .int(Int64(MetadataMessageType.data.rawValue)))
    try! d.add("piece", .int(Int64(piece)))
    try! d.add("total_size", .int(Int64(totalSize)))
    var out = bencodeEncode(.dict(d))
    out.append(contentsOf: data)
    return out
}

/// Builds a ut_metadata reject message.
public func buildMetadataReject(_ piece: Int) -> [UInt8] {
    let d = BDict()
    try! d.add("msg_type", .int(Int64(MetadataMessageType.reject.rawValue)))
    try! d.add("piece", .int(Int64(piece)))
    return bencodeEncode(.dict(d))
}

/// A parsed ut_metadata message.
public struct MetadataMessage {
    public var type: MetadataMessageType
    public var piece: Int
    public var totalSize: Int
    public var data: [UInt8]
}

/// Parses a ut_metadata message, splitting the trailing raw piece bytes from the
/// leading bencode dict.
public func parseMetadata(_ body: [UInt8]) throws -> MetadataMessage {
    let (v, n) = try bencodeDecodeN(body, 0)
    let d = try v.dictValue()
    var type = MetadataMessageType.request
    var piece = 0
    var totalSize = 0
    if let t = d.get("msg_type"), let ti = try? t.intValue() {
        type = MetadataMessageType(rawValue: Int(ti)) ?? .request
    }
    if let p = d.get("piece"), let pi = try? p.intValue() {
        piece = Int(pi)
    }
    if let ts = d.get("total_size"), let tsi = try? ts.intValue() {
        totalSize = Int(tsi)
    }
    return MetadataMessage(type: type, piece: piece, totalSize: totalSize, data: Array(body[n...]))
}

/// Reassembles the info dictionary from ut_metadata pieces and verifies it against
/// the expected info-hash.
public final class MetadataAssembler {
    private let totalSize: Int
    private var pieces: [Int: [UInt8]] = [:]

    /// Creates an assembler for a metadata of `totalSize` bytes.
    public init(totalSize: Int) {
        self.totalSize = totalSize
    }

    /// The number of 16 KiB pieces.
    public func pieceCount() -> Int {
        (totalSize + metadataPieceSize - 1) / metadataPieceSize
    }

    /// Stores a metadata piece.
    public func add(_ piece: Int, _ data: [UInt8]) {
        pieces[piece] = data
    }

    /// Reports whether every piece is present.
    public func isComplete() -> Bool { pieces.count == pieceCount() }

    /// Assembles the info dict and returns it if it matches `infoHash` (20 bytes).
    public func tryFinish(_ infoHash: [UInt8]) -> [UInt8]? {
        if !isComplete() { return nil }
        var out = [UInt8]()
        out.reserveCapacity(totalSize)
        for i in 0..<pieceCount() {
            out.append(contentsOf: pieces[i] ?? [])
        }
        if out.count != totalSize { return nil }
        if BTHash.sha1(out) != infoHash { return nil }
        return out
    }
}

// ── BEP-11 ut_pex ────────────────────────────────────────────────────────────

/// Builds a ut_pex message advertising added peers (compact form).
public func buildPexAdded(_ added: [PeerAddr]) -> [UInt8] {
    let d = BDict()
    try! d.add("added", .bytes(encodeCompactPeers(added)))
    return bencodeEncode(.dict(d))
}

/// Parses the "added" peers from a ut_pex message.
public func parsePexAdded(_ body: [UInt8]) throws -> [PeerAddr] {
    let v = try bencodeDecode(body)
    let d = try v.dictValue()
    if let a = d.get("added") {
        let b = try a.bytesValue()
        return try decodeCompactPeers(b)
    }
    return []
}
