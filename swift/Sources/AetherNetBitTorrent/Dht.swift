// SPDX-License-Identifier: MIT

import Foundation

/// A 160-bit Kademlia node identifier (BEP-5). Backed by 20 raw bytes.
public struct NodeID: Equatable {
    public var bytes: [UInt8]  // exactly 20 bytes

    public init() {
        self.bytes = [UInt8](repeating: 0, count: 20)
    }

    public init(_ bytes: [UInt8]) {
        precondition(bytes.count == 20, "NodeID must be 20 bytes")
        self.bytes = bytes
    }

    /// The XOR distance between two node ids.
    public func distanceTo(_ b: NodeID) -> NodeID {
        var d = [UInt8](repeating: 0, count: 20)
        for i in 0..<20 { d[i] = bytes[i] ^ b.bytes[i] }
        return NodeID(d)
    }

    /// Orders node ids / distances by unsigned big-endian value.
    public func compare(_ b: NodeID) -> Int { compareBytes(bytes, b.bytes) }

    /// Counts the leading zero bits (0..160). UInt8.leadingZeroBitCount is already
    /// within the 8-bit width (0..8), matching Go's bits.LeadingZeros8.
    public func leadingZeros() -> Int {
        for (i, by) in bytes.enumerated() {
            if by != 0 {
                return i * 8 + by.leadingZeroBitCount
            }
        }
        return 160
    }
}

/// A routable DHT node: its id and IPv4 endpoint.
public struct DhtContact: Equatable {
    public var id: NodeID
    public var ip: [UInt8]   // 4 bytes (IPv4)
    public var port: UInt16

    public init(id: NodeID, ip: [UInt8], port: UInt16) {
        self.id = id
        self.ip = ip
        self.port = port
    }
}

/// An IPv4 peer endpoint (compact peer, BEP-23).
public struct PeerAddr: Equatable {
    public var ip: [UInt8]   // 4 bytes (IPv4)
    public var port: UInt16

    public init(ip: [UInt8], port: UInt16) {
        self.ip = ip
        self.port = port
    }

    /// Parses a dotted-quad "a.b.c.d" address, or nil if malformed.
    public init?(ipString: String, port: UInt16) {
        let parts = ipString.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var octets = [UInt8]()
        for p in parts {
            guard let v = UInt8(p) else { return nil }
            octets.append(v)
        }
        self.ip = octets
        self.port = port
    }
}

public enum DhtError: Error, Equatable {
    case invalid(String)
}

/// Serializes contacts as 26-byte records (20 id + 4 IPv4 + 2 port BE).
public func encodeCompactNodes(_ contacts: [DhtContact]) -> [UInt8] {
    var out = [UInt8]()
    out.reserveCapacity(contacts.count * 26)
    for c in contacts {
        out.append(contentsOf: c.id.bytes)
        out.append(contentsOf: c.ip)
        var p = [UInt8](repeating: 0, count: 2)
        putUInt16BE(c.port, into: &p, at: 0)
        out.append(contentsOf: p)
    }
    return out
}

/// Parses 26-byte compact node records.
public func decodeCompactNodes(_ data: [UInt8]) throws -> [DhtContact] {
    if data.count % 26 != 0 {
        throw DhtError.invalid("compact nodes length \(data.count) is not a multiple of 26")
    }
    var out = [DhtContact]()
    out.reserveCapacity(data.count / 26)
    var i = 0
    while i < data.count {
        let id = NodeID(Array(data[i..<(i + 20)]))
        let ip = Array(data[(i + 20)..<(i + 24)])
        let port = readUInt16BE(data, at: i + 24)
        out.append(DhtContact(id: id, ip: ip, port: port))
        i += 26
    }
    return out
}

/// Serializes peers as 6-byte records (4 IPv4 + 2 port BE).
public func encodeCompactPeers(_ peers: [PeerAddr]) -> [UInt8] {
    var out = [UInt8]()
    out.reserveCapacity(peers.count * 6)
    for p in peers {
        out.append(contentsOf: p.ip)
        var b = [UInt8](repeating: 0, count: 2)
        putUInt16BE(p.port, into: &b, at: 0)
        out.append(contentsOf: b)
    }
    return out
}

/// Parses 6-byte compact peer records.
public func decodeCompactPeers(_ data: [UInt8]) throws -> [PeerAddr] {
    if data.count % 6 != 0 {
        throw DhtError.invalid("compact peers length \(data.count) is not a multiple of 6")
    }
    var out = [PeerAddr]()
    out.reserveCapacity(data.count / 6)
    var i = 0
    while i < data.count {
        let ip = Array(data[i..<(i + 4)])
        let port = readUInt16BE(data, at: i + 4)
        out.append(PeerAddr(ip: ip, port: port))
        i += 6
    }
    return out
}

/// The Kademlia bucket size.
public let dhtK = 8

/// A Kademlia routing table of 160 k-buckets indexed by shared prefix length.
public final class RoutingTable {
    private let selfID: NodeID
    private var buckets: [[DhtContact]]

    /// Creates a routing table for the local node id.
    public init(selfID: NodeID) {
        self.selfID = selfID
        self.buckets = Array(repeating: [], count: 160)
    }

    private func bucketIndex(_ id: NodeID) -> Int {
        let lz = selfID.distanceTo(id).leadingZeros()
        return lz >= 160 ? 159 : lz
    }

    /// Inserts or refreshes a contact; returns false if it is us or the bucket is full.
    @discardableResult
    public func tryAdd(_ c: DhtContact) -> Bool {
        if c.id == selfID { return false }
        let idx = bucketIndex(c.id)
        for i in 0..<buckets[idx].count where buckets[idx][i].id == c.id {
            buckets[idx][i] = c
            return true
        }
        if buckets[idx].count < dhtK {
            buckets[idx].append(c)
            return true
        }
        return false
    }

    /// Returns up to `count` contacts nearest to `target` by XOR distance.
    public func closestTo(_ target: NodeID, _ count: Int) -> [DhtContact] {
        var all = [DhtContact]()
        for b in buckets { all.append(contentsOf: b) }
        all.sort { a, b in
            a.id.distanceTo(target).compare(b.id.distanceTo(target)) < 0
        }
        if count < all.count {
            all = Array(all[0..<count])
        }
        return all
    }

    /// The total number of contacts.
    public func count() -> Int {
        buckets.reduce(0) { $0 + $1.count }
    }
}
