// SPDX-License-Identifier: MIT

import Foundation

/// Universal Hardware Identifier node representation.
public struct AetherMeshNode: Equatable, Codable {
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
public struct PeerInfo: Equatable, Codable, Sendable {
    public let uhid: String
    public let lastSeen: Date
    public let hopCount: Int
    public let reliabilityScore: Int
    public let capabilities: UInt8
    public let geohash: String?
    public let isBlocked: Bool

    public init(
        uhid: String,
        lastSeen: Date = Date(),
        hopCount: Int = 0,
        reliabilityScore: Int = 50,
        capabilities: UInt8 = 0,
        geohash: String? = nil,
        isBlocked: Bool = false
    ) {
        self.uhid = uhid
        self.lastSeen = lastSeen
        self.hopCount = hopCount
        self.reliabilityScore = reliabilityScore
        self.capabilities = capabilities
        self.geohash = geohash
        self.isBlocked = isBlocked
    }
}

/// Route table entry.
public struct RouteEntry: Equatable, Codable, Sendable {
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

/// Pre-key bundle published by a node so others can initiate Signal sessions
/// toward it asynchronously.
///
/// Two identity keys per node — Ed25519 for signing and X25519 for ECDH.
/// Keeping them separate (rather than using XEdDSA) is the simpler choice
/// across the 8-language implementation family.
public struct PreKeyBundle: Equatable, Codable {
    public let uhid: String
    /// Long-term Ed25519 identity public key (32 bytes).
    public let identityKey: Data
    /// Long-term X25519 identity public key (32 bytes raw, RFC 7748).
    public let identityKeyX25519: Data
    public let preKeyId: Int32
    /// One-time pre-key X25519 public key (32 bytes raw).
    public let preKey: Data
    public let signedPreKeyId: Int32
    /// Signed pre-key X25519 public key (32 bytes raw).
    public let signedPreKey: Data
    /// Ed25519 signature over signedPreKey (64 bytes).
    public let signedPreKeySignature: Data

    public init(
        uhid: String,
        identityKey: Data,
        identityKeyX25519: Data,
        preKeyId: Int32,
        preKey: Data,
        signedPreKeyId: Int32,
        signedPreKey: Data,
        signedPreKeySignature: Data
    ) {
        self.uhid = uhid
        self.identityKey = identityKey
        self.identityKeyX25519 = identityKeyX25519
        self.preKeyId = preKeyId
        self.preKey = preKey
        self.signedPreKeyId = signedPreKeyId
        self.signedPreKey = signedPreKey
        self.signedPreKeySignature = signedPreKeySignature
    }
}

/// Wire-level encrypted payload.
///
/// Two layered ratchets contribute fields:
///
/// 1. **X3DH session-establishment** (Signal §3) — populated only on PreKey
///    messages (`messageType == 1`): `initiatorIdentityKeyX25519`,
///    `usedSignedPreKeyId`, `usedOneTimePreKeyId`. The responder uses these
///    to run X3DH on its side and derive the same root key.
///
/// 2. **Double Ratchet** (Signal §5) — `senderEphemeralKeyX25519` and
///    `previousChainCount` populated on EVERY message. The
///    `senderEphemeralKeyX25519` is the sender's current DH-ratchet public
///    key; when it changes between messages, the receiver runs a DH-ratchet
///    step that re-keys the chain and gives per-roundtrip forward secrecy
///    and post-compromise security. On the very first PreKey message, this
///    equals the X3DH ephemeral public key (Signal-canonical integration:
///    initiator's X3DH ephemeral becomes its first DH-ratchet public).
///
/// `initiatorEphemeralKeyX25519` is retained as a deprecated alias for
/// `senderEphemeralKeyX25519` on PreKey messages — backward compatibility
/// with consumers of the pre-Double-Ratchet wire envelope. New consumers
/// should read `senderEphemeralKeyX25519` and fall back to it only if the
/// new field is nil.
public struct EncryptedPayload: Equatable, Codable {
    public let ciphertext: Data
    public let nonce: Data
    public let messageType: Int32
    public let senderUhid: String
    public let counter: Int32

    /// PreKey messages: initiator's long-term X25519 identity public key (32 bytes).
    public let initiatorIdentityKeyX25519: Data?
    /// DEPRECATED: use `senderEphemeralKeyX25519` instead. Kept for backward
    /// compatibility with the pre-Double-Ratchet wire envelope. On PreKey
    /// messages this equals `senderEphemeralKeyX25519`; on normal messages
    /// it is nil. New consumers should ignore this field.
    public let initiatorEphemeralKeyX25519: Data?
    /// PreKey messages: SignedPreKeyId from the recipient bundle the initiator consumed.
    public let usedSignedPreKeyId: Int32
    /// PreKey messages: one-time PreKeyId from the recipient bundle the initiator consumed.
    public let usedOneTimePreKeyId: Int32

    /// Sender's current DH-ratchet X25519 public key (32 bytes). Populated on
    /// every message. Drives the DH-ratchet step on the receiver side: when
    /// this value changes, the receiver re-keys the chain via
    /// `KDF_RK(rootKey, DH(myDHs, newDHr))`.
    public let senderEphemeralKeyX25519: Data?

    /// Number of messages the sender sent in its previous sending chain
    /// (Signal §5: PN). Used by the receiver to compute skipped message keys
    /// when crossing a DH-ratchet boundary.
    public let previousChainCount: Int32

    public init(
        ciphertext: Data,
        nonce: Data,
        messageType: Int32 = 0,
        senderUhid: String = "",
        counter: Int32 = 0,
        initiatorIdentityKeyX25519: Data? = nil,
        initiatorEphemeralKeyX25519: Data? = nil,
        usedSignedPreKeyId: Int32 = 0,
        usedOneTimePreKeyId: Int32 = 0,
        senderEphemeralKeyX25519: Data? = nil,
        previousChainCount: Int32 = 0
    ) {
        self.ciphertext = ciphertext
        self.nonce = nonce
        self.messageType = messageType
        self.senderUhid = senderUhid
        self.counter = counter
        self.initiatorIdentityKeyX25519 = initiatorIdentityKeyX25519
        self.initiatorEphemeralKeyX25519 = initiatorEphemeralKeyX25519
        self.usedSignedPreKeyId = usedSignedPreKeyId
        self.usedOneTimePreKeyId = usedOneTimePreKeyId
        self.senderEphemeralKeyX25519 = senderEphemeralKeyX25519
        self.previousChainCount = previousChainCount
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

// ─────────────────────────────────────────────────────────
// DTN status / priority enums + custody record
// ─────────────────────────────────────────────────────────

public enum BundleStatus: Int32, Codable, Sendable {
    case pending = 0
    case inCustody = 1
    case delivered = 2
    case expired = 3
    case failed = 4
}

public enum BundlePriority: Int32, Codable, Sendable {
    case low = 0
    case normal = 1
    case high = 2
    case sos = 3
}

/// Record of a custody transfer between two nodes.
public struct CustodyRecord: Equatable, Codable, Sendable {
    public let id: UUID
    public let bundleId: UUID
    public let fromUhid: String
    public let toUhid: String
    public let accepted: Bool
    public let transferredAt: Date

    public init(
        id: UUID = UUID(),
        bundleId: UUID,
        fromUhid: String,
        toUhid: String,
        accepted: Bool,
        transferredAt: Date = Date()
    ) {
        self.id = id
        self.bundleId = bundleId
        self.fromUhid = fromUhid
        self.toUhid = toUhid
        self.accepted = accepted
        self.transferredAt = transferredAt
    }
}

extension DtnBundle {
    public var isExpired: Bool { Date() >= expiresAt }
}

// ─────────────────────────────────────────────────────────
// SOS observed/local alert
// ─────────────────────────────────────────────────────────

/// An SOS alert observed on the mesh — locally originated or received.
public struct SosAlert: Equatable, Codable, Sendable {
    public let id: UUID
    public let senderUhid: String
    public let broadcastType: String
    public let message: String?
    public let latitude: Double
    public let longitude: Double
    public let geohash: String?
    public let receivedAt: Date

    public init(
        id: UUID = UUID(),
        senderUhid: String,
        broadcastType: String = "sos",
        message: String? = nil,
        latitude: Double = 0,
        longitude: Double = 0,
        geohash: String? = nil,
        receivedAt: Date = Date()
    ) {
        self.id = id
        self.senderUhid = senderUhid
        self.broadcastType = broadcastType
        self.message = message
        self.latitude = latitude
        self.longitude = longitude
        self.geohash = geohash
        self.receivedAt = receivedAt
    }
}

// ─────────────────────────────────────────────────────────
// Capability bit constants (matches NodeCapabilities elsewhere)
// ─────────────────────────────────────────────────────────

public enum NodeCapabilityBits {
    public static let ble: UInt8 = 1
    public static let wifiDirect: UInt8 = 2
    public static let gateway: UInt8 = 4
    public static let relay: UInt8 = 8
    public static let sos: UInt8 = 16
    public static let streaming: UInt8 = 32
    public static let voice: UInt8 = 64
    public static let dtnCarrier: UInt8 = 128
}
