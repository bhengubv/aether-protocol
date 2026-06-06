// SPDX-License-Identifier: MIT

package aethermesh.protocol

import aethermesh.AetherMeshConstants
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.*

/**
 * The core packet transmitted across the Aether mesh network.
 * Every piece of data — route discovery, messages, SOS broadcasts, voice,
 * streaming, DTN bundles — travels as a MeshPacket.
 *
 * Wire format is compatible with the C# implementation.
 */
data class MeshPacket(
    var id: UUID = UUID.randomUUID(),
    var type: PacketType = PacketType.Data,
    var sourceUhid: String = "",
    var destinationUhid: String = "",
    var ttl: Int = AetherMeshConstants.DEFAULT_TTL,
    var priority: Byte = 0,
    var payload: ByteArray = ByteArray(0),
    var createdAt: Long = System.currentTimeMillis(),
    var signature: ByteArray = ByteArray(0),
    var packetNonce: ByteArray = ByteArray(0),
    var timestampMs: Long = System.currentTimeMillis(),
    var protocolVersion: Byte = AetherMeshConstants.PROTOCOL_VERSION_CURRENT.toByte()
) {
    /**
     * Returns true if this packet has exceeded the maximum allowed age.
     */
    fun isExpired(maxAgeSeconds: Int = AetherMeshConstants.MAX_PACKET_AGE_SECONDS): Boolean {
        val ageMs = System.currentTimeMillis() - timestampMs
        return ageMs > maxAgeSeconds * 1000L
    }

    /**
     * Returns true if the packet can still be forwarded (TTL > 0).
     */
    fun canForward(): Boolean = ttl > 0

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is MeshPacket) return false

        if (id != other.id) return false
        if (type != other.type) return false
        if (sourceUhid != other.sourceUhid) return false
        if (destinationUhid != other.destinationUhid) return false
        if (ttl != other.ttl) return false
        if (priority != other.priority) return false
        if (!payload.contentEquals(other.payload)) return false
        if (createdAt != other.createdAt) return false
        if (!signature.contentEquals(other.signature)) return false
        if (!packetNonce.contentEquals(other.packetNonce)) return false
        if (timestampMs != other.timestampMs) return false
        if (protocolVersion != other.protocolVersion) return false

        return true
    }

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + type.hashCode()
        result = 31 * result + sourceUhid.hashCode()
        result = 31 * result + destinationUhid.hashCode()
        result = 31 * result + ttl
        result = 31 * result + priority
        result = 31 * result + payload.contentHashCode()
        result = 31 * result + createdAt.hashCode()
        result = 31 * result + signature.contentHashCode()
        result = 31 * result + packetNonce.contentHashCode()
        result = 31 * result + timestampMs.hashCode()
        result = 31 * result + protocolVersion
        return result
    }

    override fun toString(): String {
        return "[${type.name}] ${id} src=$sourceUhid dst=$destinationUhid ttl=$ttl pri=$priority ver=$protocolVersion"
    }
}
