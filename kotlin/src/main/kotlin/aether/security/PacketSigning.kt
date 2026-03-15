// SPDX-License-Identifier: MIT

package aether.security

import aether.protocol.MeshPacket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

/**
 * Packet signing utilities with nonce deduplication.
 *
 * Constructs signable data matching the C# implementation exactly:
 * PacketNonce || TimestampMs || Type || SourceUhidLength || SourceUhid ||
 * DestinationUhidLength || DestinationUhid || SHA-256(Payload) || Ttl || Priority
 */
object PacketSigning {
    private val nonceDedupCache = ConcurrentHashMap<Pair<String, ByteArray>, Long>()
    private const val MAX_PACKET_AGE_MS = 300_000L // 5 minutes

    /**
     * Constructs the signable data for a packet.
     *
     * Wire format (little-endian):
     *   PacketNonce (8 bytes)
     *   TimestampMs (8 bytes, int64)
     *   Type (4 bytes, int32)
     *   SourceUhidLength (4 bytes, int32)
     *   SourceUhid (UTF-8 bytes)
     *   DestinationUhidLength (4 bytes, int32)
     *   DestinationUhid (UTF-8 bytes)
     *   SHA-256(Payload) (32 bytes)
     *   Ttl (4 bytes, int32)
     *   Priority (4 bytes, int32)
     */
    fun constructSignableData(packet: MeshPacket): ByteArray {
        val sourceBytes = packet.sourceUhid.toByteArray(Charsets.UTF_8)
        val destBytes = packet.destinationUhid.toByteArray(Charsets.UTF_8)
        val payloadHash = computeSHA256(packet.payload)

        val buffer = ByteBuffer.allocate(
            8 +  // PacketNonce
                    8 +  // TimestampMs
                    4 +  // Type
                    4 + sourceBytes.size +  // SourceUhid
                    4 + destBytes.size +  // DestinationUhid
                    32 +  // SHA-256(Payload)
                    4 +  // Ttl
                    4    // Priority
        ).apply {
            order(ByteOrder.LITTLE_ENDIAN)
        }

        // PacketNonce
        buffer.put(packet.packetNonce)

        // TimestampMs
        buffer.putLong(packet.timestampMs)

        // Type
        buffer.putInt(packet.type.value.toInt())

        // SourceUhid length and data
        buffer.putInt(sourceBytes.size)
        buffer.put(sourceBytes)

        // DestinationUhid length and data
        buffer.putInt(destBytes.size)
        buffer.put(destBytes)

        // SHA-256(Payload)
        buffer.put(payloadHash)

        // Ttl
        buffer.putInt(packet.ttl)

        // Priority
        buffer.putInt(packet.priority.toInt())

        return buffer.array()
    }

    /**
     * Signs a packet using Ed25519.
     *
     * @param packet The packet to sign
     * @param privateKey 32-byte Ed25519 private key
     * @return 64-byte signature
     */
    fun signPacket(packet: MeshPacket, privateKey: ByteArray): ByteArray {
        val signableData = constructSignableData(packet)
        return Ed25519Service.sign(privateKey, signableData)
    }

    /**
     * Verifies a packet signature using Ed25519.
     *
     * @param packet The packet to verify
     * @param publicKey 32-byte Ed25519 public key
     * @return True if the signature is valid
     */
    fun verifyPacket(packet: MeshPacket, publicKey: ByteArray): Boolean {
        val signableData = constructSignableData(packet)
        return Ed25519Service.verify(publicKey, signableData, packet.signature)
    }

    /**
     * Checks if a packet nonce has been seen before (replay prevention).
     * Returns true if the nonce is NEW (not a replay).
     *
     * Maintains a deduplication cache with a 5-minute TTL.
     *
     * @param packet The packet to check
     * @return True if this is a new packet, false if it's a replay
     */
    fun isNewPacket(packet: MeshPacket): Boolean {
        val key = Pair(packet.sourceUhid, packet.packetNonce)
        val now = System.currentTimeMillis()

        // Clean old entries
        nonceDedupCache.entries.removeAll { (_, timestamp) ->
            (now - timestamp) > MAX_PACKET_AGE_MS
        }

        // Check if nonce exists
        val existing = nonceDedupCache[key]
        if (existing != null) {
            return false // Replay detected
        }

        // New nonce
        nonceDedupCache[key] = now
        return true
    }

    /**
     * Computes SHA-256 hash of data.
     */
    private fun computeSHA256(data: ByteArray): ByteArray {
        val digest = MessageDigest.getInstance("SHA-256")
        return digest.digest(data)
    }
}
