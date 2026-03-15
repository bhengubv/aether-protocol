// SPDX-License-Identifier: MIT

import Foundation

/// Binary serializer/deserializer for MeshPacket.
///
/// Wire format (all multi-byte integers are little-endian):
///
///   [1 byte]   Protocol version
///   [1 byte]   Packet type
///   [16 bytes] Packet ID (UUID)
///   [1 byte]   Priority
///   [4 bytes]  TTL (Int32)
///   [8 bytes]  TimestampMs (Int64)
///   [2 bytes]  SourceUhid length (UInt16)
///   [N bytes]  SourceUhid (UTF-8)
///   [2 bytes]  DestinationUhid length (UInt16)
///   [N bytes]  DestinationUhid (UTF-8)
///   [2 bytes]  PacketNonce length (UInt16)
///   [N bytes]  PacketNonce
///   [4 bytes]  Payload length (Int32)
///   [N bytes]  Payload
///   [2 bytes]  Signature length (UInt16)
///   [N bytes]  Signature
public enum PacketSerializer {
    /// Serializes a MeshPacket to its binary wire format.
    public static func serialize(_ packet: MeshPacket) -> Data {
        var buffer = Data()

        // Protocol version
        buffer.append(packet.protocolVersion)

        // Packet type
        buffer.append(packet.type.rawValue)

        // Packet ID (16 bytes)
        var uuid = packet.id.uuid
        withUnsafeBytes(of: &uuid) { buffer.append(contentsOf: $0) }

        // Priority
        buffer.append(packet.priority)

        // TTL (Int32, little-endian)
        var ttl = Int32(packet.ttl).littleEndian
        withUnsafeBytes(of: &ttl) { buffer.append(contentsOf: $0) }

        // TimestampMs (Int64, little-endian)
        var timestamp = packet.timestampMs.littleEndian
        withUnsafeBytes(of: &timestamp) { buffer.append(contentsOf: $0) }

        // SourceUhid
        let sourceBytes = packet.sourceUhid.data(using: .utf8) ?? Data()
        var sourceLen = UInt16(sourceBytes.count).littleEndian
        withUnsafeBytes(of: &sourceLen) { buffer.append(contentsOf: $0) }
        buffer.append(sourceBytes)

        // DestinationUhid
        let destBytes = packet.destinationUhid.data(using: .utf8) ?? Data()
        var destLen = UInt16(destBytes.count).littleEndian
        withUnsafeBytes(of: &destLen) { buffer.append(contentsOf: $0) }
        buffer.append(destBytes)

        // PacketNonce
        var nonceLen = UInt16(packet.packetNonce.count).littleEndian
        withUnsafeBytes(of: &nonceLen) { buffer.append(contentsOf: $0) }
        buffer.append(packet.packetNonce)

        // Payload
        var payloadLen = Int32(packet.payload.count).littleEndian
        withUnsafeBytes(of: &payloadLen) { buffer.append(contentsOf: $0) }
        buffer.append(packet.payload)

        // Signature
        var sigLen = UInt16(packet.signature.count).littleEndian
        withUnsafeBytes(of: &sigLen) { buffer.append(contentsOf: $0) }
        buffer.append(packet.signature)

        return buffer
    }

    /// Deserializes a MeshPacket from its binary wire format.
    public static func deserialize(_ data: Data) throws -> MeshPacket {
        guard data.count >= 43 else {
            throw PacketSerializationError.dataTooShort(
                "Data is too short to contain a valid MeshPacket. Minimum 43 bytes, got \(data.count)."
            )
        }

        var offset = 0

        // Protocol version
        let protocolVersion = data[offset]
        offset += 1

        // Packet type
        guard let type = PacketType(rawValue: data[offset]) else {
            throw PacketSerializationError.invalidPacketType(data[offset])
        }
        offset += 1

        // Packet ID (16 bytes)
        let uuidBytes = data.subdata(in: offset ..< offset + 16)
        let uuid = UUID(uuid: uuidBytes.withUnsafeBytes({ $0.load(as: uuid_t.self) }))
        offset += 16

        // Priority
        let priority = data[offset]
        offset += 1

        // TTL (Int32, little-endian)
        let ttlBytes = data.subdata(in: offset ..< offset + 4)
        let ttl = UInt8(Int32(littleEndian: ttlBytes.withUnsafeBytes { $0.load(as: Int32.self) }))
        offset += 4

        // TimestampMs (Int64, little-endian)
        let timestampBytes = data.subdata(in: offset ..< offset + 8)
        let timestamp = Int64(littleEndian: timestampBytes.withUnsafeBytes { $0.load(as: Int64.self) })
        offset += 8

        // SourceUhid
        let sourceLen = Int(UInt16(littleEndian: data.subdata(in: offset ..< offset + 2).withUnsafeBytes { $0.load(as: UInt16.self) }))
        offset += 2
        guard offset + sourceLen <= data.count else {
            throw PacketSerializationError.insufficientData(need: sourceLen, have: data.count - offset)
        }
        let sourceUhid = String(data: data.subdata(in: offset ..< offset + sourceLen), encoding: .utf8) ?? ""
        offset += sourceLen

        // DestinationUhid
        let destLen = Int(UInt16(littleEndian: data.subdata(in: offset ..< offset + 2).withUnsafeBytes { $0.load(as: UInt16.self) }))
        offset += 2
        guard offset + destLen <= data.count else {
            throw PacketSerializationError.insufficientData(need: destLen, have: data.count - offset)
        }
        let destinationUhid = String(data: data.subdata(in: offset ..< offset + destLen), encoding: .utf8) ?? ""
        offset += destLen

        // PacketNonce
        let nonceLen = Int(UInt16(littleEndian: data.subdata(in: offset ..< offset + 2).withUnsafeBytes { $0.load(as: UInt16.self) }))
        offset += 2
        guard offset + nonceLen <= data.count else {
            throw PacketSerializationError.insufficientData(need: nonceLen, have: data.count - offset)
        }
        let packetNonce = data.subdata(in: offset ..< offset + nonceLen)
        offset += nonceLen

        // Payload
        let payloadLen = Int(Int32(littleEndian: data.subdata(in: offset ..< offset + 4).withUnsafeBytes { $0.load(as: Int32.self) }))
        offset += 4
        guard payloadLen >= 0 else {
            throw PacketSerializationError.negativeLength("Negative payload length: \(payloadLen)")
        }
        guard offset + payloadLen <= data.count else {
            throw PacketSerializationError.insufficientData(need: payloadLen, have: data.count - offset)
        }
        let payload = data.subdata(in: offset ..< offset + payloadLen)
        offset += payloadLen

        // Signature
        let sigLen = Int(UInt16(littleEndian: data.subdata(in: offset ..< offset + 2).withUnsafeBytes { $0.load(as: UInt16.self) }))
        offset += 2
        guard offset + sigLen <= data.count else {
            throw PacketSerializationError.insufficientData(need: sigLen, have: data.count - offset)
        }
        let signature = data.subdata(in: offset ..< offset + sigLen)

        let createdAt = Date(timeIntervalSince1970: TimeInterval(timestamp) / 1000.0)

        return MeshPacket(
            id: uuid,
            type: type,
            sourceUhid: sourceUhid,
            destinationUhid: destinationUhid,
            ttl: ttl,
            priority: priority,
            payload: payload,
            createdAt: createdAt,
            signature: signature,
            packetNonce: packetNonce,
            timestampMs: timestamp,
            protocolVersion: protocolVersion
        )
    }

    /// Attempts to deserialize a packet, returning nil on failure.
    public static func tryDeserialize(_ data: Data) -> MeshPacket? {
        try? deserialize(data)
    }
}

public enum PacketSerializationError: Error, Equatable {
    case dataTooShort(String)
    case invalidPacketType(UInt8)
    case invalidUuid
    case insufficientData(need: Int, have: Int)
    case negativeLength(String)

    public var localizedDescription: String {
        switch self {
        case .dataTooShort(let msg):
            return "Data too short: \(msg)"
        case .invalidPacketType(let type):
            return "Invalid packet type: \(type)"
        case .invalidUuid:
            return "Invalid UUID format"
        case .insufficientData(let need, let have):
            return "Insufficient data: need \(need) bytes, have \(have)"
        case .negativeLength(let msg):
            return msg
        }
    }
}
