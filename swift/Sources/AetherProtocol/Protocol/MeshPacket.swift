// SPDX-License-Identifier: MIT

import Foundation

/// The core packet transmitted across the Aether mesh network.
/// Every piece of data — route discovery, messages, SOS broadcasts, voice,
/// streaming, DTN bundles — travels as a MeshPacket.
public struct MeshPacket: Equatable {
    /// Unique identifier for this packet.
    public var id: UUID

    /// The type of packet, determining how the payload is interpreted.
    public var type: PacketType

    /// Universal Hardware Identifier of the source node.
    public var sourceUhid: String

    /// Universal Hardware Identifier of the destination node. Empty for broadcast.
    public var destinationUhid: String

    /// Time-to-live: decremented at each hop. Packet is dropped when TTL reaches 0.
    public var ttl: UInt8

    /// Priority level (higher = more urgent). SOS packets use priority 255.
    public var priority: UInt8

    /// The packet payload. Interpretation depends on `type`.
    public var payload: Data

    /// UTC timestamp when this packet was created.
    public var createdAt: Date

    /// Cryptographic signature over the packet contents, produced by the source node.
    public var signature: Data

    /// Random nonce to prevent replay attacks. Must be unique per packet.
    public var packetNonce: Data

    /// Unix timestamp in milliseconds, used for age-based deduplication.
    public var timestampMs: Int64

    /// Protocol version. Current version is 2.
    public var protocolVersion: UInt8

    public init(
        id: UUID = UUID(),
        type: PacketType,
        sourceUhid: String = "",
        destinationUhid: String = "",
        ttl: UInt8 = ProtocolConstants.defaultTtl,
        priority: UInt8 = 0,
        payload: Data = Data(),
        createdAt: Date = Date(),
        signature: Data = Data(),
        packetNonce: Data = Data(),
        timestampMs: Int64 = Int64(Date().timeIntervalSince1970 * 1000),
        protocolVersion: UInt8 = ProtocolConstants.protocolVersionSigned
    ) {
        self.id = id
        self.type = type
        self.sourceUhid = sourceUhid
        self.destinationUhid = destinationUhid
        self.ttl = ttl
        self.priority = priority
        self.payload = payload
        self.createdAt = createdAt
        self.signature = signature
        self.packetNonce = packetNonce
        self.timestampMs = timestampMs
        self.protocolVersion = protocolVersion
    }

    /// Returns true if this packet has exceeded the maximum allowed age.
    public func isExpired(maxAgeSeconds: Int = ProtocolConstants.maxPacketAgeSeconds) -> Bool {
        let ageMs = Int64(Date().timeIntervalSince1970 * 1000) - timestampMs
        return ageMs > Int64(maxAgeSeconds * 1000)
    }

    /// Returns true if the packet can still be forwarded (TTL > 0).
    public var canForward: Bool {
        ttl > 0
    }

    public var description: String {
        "[\(type)] \(id.uuidString.prefix(8)) src=\(sourceUhid) dst=\(destinationUhid) ttl=\(ttl) pri=\(priority) ver=\(protocolVersion)"
    }
}
