// SPDX-License-Identifier: MIT

import Foundation

/// Universal Hardware Identifier node representation.
public struct AetherNode: Equatable, Codable {
    public let uhid: String
    public let identityPublicKey: Data
    public let capabilities: UInt8
    public let discoveredAt: Date

    public init(
        uhid: String,
        identityPublicKey: Data,
        capabilities: UInt8 = 0,
        discoveredAt: Date = Date()
    ) {
        self.uhid = uhid
        self.identityPublicKey = identityPublicKey
        self.capabilities = capabilities
        self.discoveredAt = discoveredAt
    }
}

/// Information about a peer in the mesh.
public struct PeerInfo: Equatable, Codable {
    public let uhid: String
    public let lastSeen: Date
    public let hopCount: Int
    public let reliabilityScore: Int
    public let capabilities: UInt8

    public init(
        uhid: String,
        lastSeen: Date = Date(),
        hopCount: Int = 0,
        reliabilityScore: Int = 50,
        capabilities: UInt8 = 0
    ) {
        self.uhid = uhid
        self.lastSeen = lastSeen
        self.hopCount = hopCount
        self.reliabilityScore = reliabilityScore
        self.capabilities = capabilities
    }
}

/// Route table entry.
public struct RouteEntry: Equatable, Codable {
    public let destination: String
    public let nextHop: String
    public let hopCount: Int
    public let expiresAt: Date
    public let qualityScore: Int

    public init(
        destination: String,
        nextHop: String,
        hopCount: Int,
        expiresAt: Date,
        qualityScore: Int = 50
    ) {
        self.destination = destination
        self.nextHop = nextHop
        self.hopCount = hopCount
        self.expiresAt = expiresAt
        self.qualityScore = qualityScore
    }

    var isExpired: Bool {
        Date() > expiresAt
    }
}

/// Pre-key bundle for session establishment.
public struct PreKeyBundle: Equatable, Codable {
    public let uhid: String
    public let identityKey: Data  // 32-byte Ed25519 public key
    public let preKeyId: Int32
    public let preKey: Data
    public let signedPreKeyId: Int32
    public let signedPreKey: Data
    public let signedPreKeySignature: Data

    public init(
        uhid: String,
        identityKey: Data,
        preKeyId: Int32,
        preKey: Data,
        signedPreKeyId: Int32,
        signedPreKey: Data,
        signedPreKeySignature: Data
    ) {
        self.uhid = uhid
        self.identityKey = identityKey
        self.preKeyId = preKeyId
        self.preKey = preKey
        self.signedPreKeyId = signedPreKeyId
        self.signedPreKey = signedPreKey
        self.signedPreKeySignature = signedPreKeySignature
    }
}

/// Encrypted payload wrapper.
public struct EncryptedPayload: Equatable, Codable {
    public let ciphertext: Data
    public let nonce: Data
    public let messageType: Int32
    public let senderUhid: String
    public let counter: Int32

    public init(
        ciphertext: Data,
        nonce: Data,
        messageType: Int32 = 0,
        senderUhid: String = "",
        counter: Int32 = 0
    ) {
        self.ciphertext = ciphertext
        self.nonce = nonce
        self.messageType = messageType
        self.senderUhid = senderUhid
        self.counter = counter
    }
}

/// DTN bundle representation.
public struct DtnBundle: Equatable, Codable {
    public let id: UUID
    public let senderUhid: String
    public let recipientUhid: String
    public let encryptedPayload: Data
    public let priority: Int32
    public let status: Int32  // 0=Pending, 1=InCustody, 2=Delivered, 3=Expired, 4=Failed
    public let copyCount: Int32
    public let maxCopies: Int32
    public let senderGeohash: String?
    public let recipientLastGeohash: String?
    public let hopCount: Int32
    public let createdAt: Date
    public let expiresAt: Date

    public init(
        id: UUID = UUID(),
        senderUhid: String,
        recipientUhid: String,
        encryptedPayload: Data,
        priority: Int32 = 1,
        status: Int32 = 0,
        copyCount: Int32 = 1,
        maxCopies: Int32 = 3,
        senderGeohash: String? = nil,
        recipientLastGeohash: String? = nil,
        hopCount: Int32 = 0,
        createdAt: Date = Date(),
        expiresAt: Date? = nil
    ) {
        self.id = id
        self.senderUhid = senderUhid
        self.recipientUhid = recipientUhid
        self.encryptedPayload = encryptedPayload
        self.priority = priority
        self.status = status
        self.copyCount = copyCount
        self.maxCopies = maxCopies
        self.senderGeohash = senderGeohash
        self.recipientLastGeohash = recipientLastGeohash
        self.hopCount = hopCount
        self.createdAt = createdAt
        self.expiresAt = expiresAt ?? Date(timeIntervalSinceNow: TimeInterval(72 * 3600))
    }
}

/// DTN delivery receipt.
public struct DtnDeliveryReceipt: Equatable, Codable {
    public let bundleId: UUID
    public let recipientUhid: String
    public let totalHops: Int32
    public let totalCustodyTransfers: Int32
    public let deliveredAt: Date

    public init(
        bundleId: UUID,
        recipientUhid: String,
        totalHops: Int32 = 0,
        totalCustodyTransfers: Int32 = 0,
        deliveredAt: Date = Date()
    ) {
        self.bundleId = bundleId
        self.recipientUhid = recipientUhid
        self.totalHops = totalHops
        self.totalCustodyTransfers = totalCustodyTransfers
        self.deliveredAt = deliveredAt
    }
}

/// SOS broadcast payload.
public struct SosBroadcastPayload: Codable {
    public let broadcastId: String
    public let broadcastType: String
    public let message: String?
    public let latitude: Double?
    public let longitude: Double?
    public let geohash: String?

    public init(
        broadcastId: String = UUID().uuidString,
        broadcastType: String = "sos",
        message: String? = nil,
        latitude: Double? = nil,
        longitude: Double? = nil,
        geohash: String? = nil
    ) {
        self.broadcastId = broadcastId
        self.broadcastType = broadcastType
        self.message = message
        self.latitude = latitude
        self.longitude = longitude
        self.geohash = geohash
    }
}
