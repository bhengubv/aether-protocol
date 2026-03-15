// SPDX-License-Identifier: MIT

import Crypto
import Foundation

/// Packet signing service with nonce deduplication for replay prevention.
public actor PacketSigningService {
    /// Maximum age for cached nonces (5 minutes).
    private let maxNonceAgeSeconds: Int = 300

    /// Deduplication cache: (senderUhid, nonce) -> timestamp
    private var nonceCache: [String: (nonce: Data, timestamp: Date)] = [:]

    private let ed25519PrivateKey: Data
    private let ed25519PublicKey: Data

    public init(privateKey: Data, publicKey: Data) {
        self.ed25519PrivateKey = privateKey
        self.ed25519PublicKey = publicKey
    }

    /// Signs a mesh packet according to the Aether protocol.
    /// The signature covers: PacketNonce || TimestampMs || Type || SourceUhidLength || SourceUhid ||
    /// DestinationUhidLength || DestinationUhid || SHA256(Payload) || Ttl || Priority
    public func signPacket(_ packet: inout MeshPacket) throws {
        let signableData = try constructSignableData(packet)
        packet.signature = try Ed25519Service.sign(ed25519PrivateKey, signableData)
    }

    /// Verifies a packet signature and checks for replay attacks.
    public func verifyPacket(
        _ packet: MeshPacket,
        againstPublicKey publicKey: Data
    ) throws -> Bool {
        // Check nonce deduplication
        try checkNonceDuplicate(packet.sourceUhid, packet.packetNonce)

        // Construct and verify signature
        let signableData = try constructSignableData(packet)
        return Ed25519Service.verify(publicKey, signableData, packet.signature)
    }

    /// Gets the public key for this signing service.
    public func getPublicKey() -> Data {
        ed25519PublicKey
    }

    // MARK: - Private Methods

    private func constructSignableData(_ packet: MeshPacket) throws -> Data {
        var data = Data()

        // PacketNonce (8 bytes)
        data.append(packet.packetNonce)

        // TimestampMs (8 bytes, little-endian)
        var timestamp = packet.timestampMs.littleEndian
        data.append(withUnsafeBytes(of: &timestamp) { Data($0) })

        // Type (4 bytes, little-endian)
        var typeValue = Int32(packet.type.rawValue).littleEndian
        data.append(withUnsafeBytes(of: &typeValue) { Data($0) })

        // SourceUhidLength (4 bytes, little-endian)
        let sourceBytes = packet.sourceUhid.data(using: .utf8) ?? Data()
        var sourceLen = Int32(sourceBytes.count).littleEndian
        data.append(withUnsafeBytes(of: &sourceLen) { Data($0) })

        // SourceUhid (UTF-8)
        data.append(sourceBytes)

        // DestinationUhidLength (4 bytes, little-endian)
        let destBytes = packet.destinationUhid.data(using: .utf8) ?? Data()
        var destLen = Int32(destBytes.count).littleEndian
        data.append(withUnsafeBytes(of: &destLen) { Data($0) })

        // DestinationUhid (UTF-8)
        data.append(destBytes)

        // SHA256(Payload) (32 bytes)
        let payloadHash = Data(SHA256.hash(data: packet.payload))
        data.append(payloadHash)

        // Ttl (4 bytes, little-endian)
        var ttlValue = Int32(packet.ttl).littleEndian
        data.append(withUnsafeBytes(of: &ttlValue) { Data($0) })

        // Priority (4 bytes, little-endian)
        var priorityValue = Int32(packet.priority).littleEndian
        data.append(withUnsafeBytes(of: &priorityValue) { Data($0) })

        return data
    }

    private func checkNonceDuplicate(_ sourceUhid: String, _ nonce: Data) throws {
        let cacheKey = sourceUhid

        // Clean up old cache entries
        let now = Date()
        let threshold = now.addingTimeInterval(-TimeInterval(maxNonceAgeSeconds))
        for (key, value) in nonceCache {
            if value.timestamp < threshold {
                nonceCache.removeValue(forKey: key)
            }
        }

        // Check for duplicate nonce
        if let cachedEntry = nonceCache[cacheKey], cachedEntry.nonce == nonce {
            throw PacketSigningError.duplicateNonce(sourceUhid)
        }

        // Add to cache
        nonceCache[cacheKey] = (nonce: nonce, timestamp: now)
    }
}

public enum PacketSigningError: Error, Equatable {
    case duplicateNonce(String)
    case signatureFailed(String)
    case verificationFailed(String)
}
