// SPDX-License-Identifier: MIT

package aether.models

import aether.AetherConstants
import java.time.Instant

/**
 * Node capabilities as a bitfield.
 */
data class NodeCapabilities(
    val ble: Boolean = false,
    val wifiDirect: Boolean = false,
    val gateway: Boolean = false,
    val relay: Boolean = false,
    val sos: Boolean = false,
    val streaming: Boolean = false,
    val voice: Boolean = false,
    val dtnCarrier: Boolean = false,
    val nearLink: Boolean = false,
    val video: Boolean = false
) {
    /**
     * Converts to a bitfield integer representation.
     */
    fun toBitfield(): Int {
        var bits = 0
        if (ble) bits = bits or 1
        if (wifiDirect) bits = bits or 2
        if (gateway) bits = bits or 4
        if (relay) bits = bits or 8
        if (sos) bits = bits or 16
        if (streaming) bits = bits or 32
        if (voice) bits = bits or 64
        if (dtnCarrier) bits = bits or 128
        if (nearLink) bits = bits or 256
        if (video) bits = bits or 512
        return bits
    }

    companion object {
        /**
         * Converts from a bitfield integer.
         */
        fun fromBitfield(bits: Int): NodeCapabilities {
            return NodeCapabilities(
                ble = (bits and 1) != 0,
                wifiDirect = (bits and 2) != 0,
                gateway = (bits and 4) != 0,
                relay = (bits and 8) != 0,
                sos = (bits and 16) != 0,
                streaming = (bits and 32) != 0,
                voice = (bits and 64) != 0,
                dtnCarrier = (bits and 128) != 0,
                nearLink = (bits and 256) != 0,
                video = (bits and 512) != 0
            )
        }
    }
}

/**
 * Information about a peer node.
 */
data class PeerInfo(
    val uhid: String,
    val identityKey: ByteArray,
    val capabilities: NodeCapabilities = NodeCapabilities(),
    val reliabilityScore: Int = 50,
    val lastSeen: Instant = Instant.now(),
    val geohash: String? = null
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PeerInfo) return false

        if (uhid != other.uhid) return false
        if (!identityKey.contentEquals(other.identityKey)) return false
        if (capabilities != other.capabilities) return false
        if (reliabilityScore != other.reliabilityScore) return false
        if (lastSeen != other.lastSeen) return false
        if (geohash != other.geohash) return false

        return true
    }

    override fun hashCode(): Int {
        var result = uhid.hashCode()
        result = 31 * result + identityKey.contentHashCode()
        result = 31 * result + capabilities.hashCode()
        result = 31 * result + reliabilityScore
        result = 31 * result + lastSeen.hashCode()
        result = 31 * result + (geohash?.hashCode() ?: 0)
        return result
    }
}

/**
 * An entry in the routing table.
 */
data class RouteEntry(
    val destinationUhid: String,
    val nextHopUhid: String,
    val hopCount: Int,
    val qualityScore: Int = 50,
    val expiresAt: Instant = Instant.now().plusSeconds(AetherConstants.ROUTE_EXPIRY_SECONDS)
) {
    /**
     * Checks if the route has expired.
     */
    fun isExpired(): Boolean = Instant.now() > expiresAt
}

/**
 * Represents an Aether mesh node.
 */
data class AetherNode(
    val uhid: String,
    val identityPublicKey: ByteArray,
    val capabilities: NodeCapabilities = NodeCapabilities(),
    val geohash: String? = null
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is AetherNode) return false

        if (uhid != other.uhid) return false
        if (!identityPublicKey.contentEquals(other.identityPublicKey)) return false
        if (capabilities != other.capabilities) return false
        if (geohash != other.geohash) return false

        return true
    }

    override fun hashCode(): Int {
        var result = uhid.hashCode()
        result = 31 * result + identityPublicKey.contentHashCode()
        result = 31 * result + capabilities.hashCode()
        result = 31 * result + (geohash?.hashCode() ?: 0)
        return result
    }
}

/**
 * DTN bundle for store-and-forward delivery.
 */
data class DtnBundle(
    val id: java.util.UUID = java.util.UUID.randomUUID(),
    val senderUhid: String,
    val recipientUhid: String,
    val encryptedPayload: ByteArray,
    val priority: Int = 1,
    val status: String = "Pending",
    val copyCount: Int = 1,
    val maxCopies: Int = AetherConstants.DTN_MAX_COPIES,
    val senderGeohash: String? = null,
    val recipientLastGeohash: String? = null,
    val hopCount: Int = 0,
    val createdAt: Instant = Instant.now(),
    val expiresAt: Instant = Instant.now().plusSeconds(AetherConstants.DTN_BUNDLE_TTL_HOURS * 3600)
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is DtnBundle) return false

        if (id != other.id) return false
        if (senderUhid != other.senderUhid) return false
        if (recipientUhid != other.recipientUhid) return false
        if (!encryptedPayload.contentEquals(other.encryptedPayload)) return false
        if (priority != other.priority) return false
        if (status != other.status) return false
        if (copyCount != other.copyCount) return false
        if (maxCopies != other.maxCopies) return false
        if (senderGeohash != other.senderGeohash) return false
        if (recipientLastGeohash != other.recipientLastGeohash) return false
        if (hopCount != other.hopCount) return false
        if (createdAt != other.createdAt) return false
        if (expiresAt != other.expiresAt) return false

        return true
    }

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + senderUhid.hashCode()
        result = 31 * result + recipientUhid.hashCode()
        result = 31 * result + encryptedPayload.contentHashCode()
        result = 31 * result + priority
        result = 31 * result + status.hashCode()
        result = 31 * result + copyCount
        result = 31 * result + maxCopies
        result = 31 * result + (senderGeohash?.hashCode() ?: 0)
        result = 31 * result + (recipientLastGeohash?.hashCode() ?: 0)
        result = 31 * result + hopCount
        result = 31 * result + createdAt.hashCode()
        result = 31 * result + expiresAt.hashCode()
        return result
    }

    fun isExpired(): Boolean = Instant.now() >= expiresAt
}

// ─────────────────────────────────────────────────────────
// DTN extras (status / priority enums + custody + receipt)
// ─────────────────────────────────────────────────────────

enum class BundleStatus(val value: Int) {
    Pending(0),
    InCustody(1),
    Delivered(2),
    Expired(3),
    Failed(4);

    companion object {
        fun fromValue(value: Int): BundleStatus = values().first { it.value == value }
    }
}

enum class BundlePriority(val value: Int) {
    Low(0),
    Normal(1),
    High(2),
    Sos(3);

    companion object {
        fun fromValue(value: Int): BundlePriority = values().first { it.value == value }
    }
}

/** A single custody-transfer record. */
data class CustodyRecord(
    val bundleId: java.util.UUID,
    val fromUhid: String,
    val toUhid: String,
    val accepted: Boolean,
    val id: java.util.UUID = java.util.UUID.randomUUID(),
    val transferredAt: Instant = Instant.now()
)

/** Receipt sent back to the original sender once a bundle is delivered. */
data class DtnDeliveryReceipt(
    val bundleId: java.util.UUID,
    val recipientUhid: String,
    val totalHops: Int,
    val totalCustodyTransfers: Int,
    val deliveredAt: Instant = Instant.now()
)

// ─────────────────────────────────────────────────────────
// SOS
// ─────────────────────────────────────────────────────────

/** An SOS alert observed on the mesh — locally originated or received. */
data class SosAlert(
    val senderUhid: String,
    val broadcastType: String = "sos",
    val message: String? = null,
    val latitude: Double = 0.0,
    val longitude: Double = 0.0,
    val geohash: String? = null,
    val id: java.util.UUID = java.util.UUID.randomUUID(),
    val receivedAt: Instant = Instant.now()
)
