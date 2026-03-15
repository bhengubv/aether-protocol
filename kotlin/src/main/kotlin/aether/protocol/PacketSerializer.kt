// SPDX-License-Identifier: MIT

package aether.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.*

/**
 * Binary serializer/deserializer for [MeshPacket].
 *
 * Wire format (all multi-byte integers are little-endian):
 *   [1 byte]  Protocol version
 *   [1 byte]  Packet type
 *   [16 bytes] Packet ID (UUID)
 *   [1 byte]  Priority
 *   [4 bytes] TTL (int32)
 *   [8 bytes] TimestampMs (int64)
 *   [2 bytes] SourceUhid length (uint16)
 *   [N bytes] SourceUhid (UTF-8)
 *   [2 bytes] DestinationUhid length (uint16)
 *   [N bytes] DestinationUhid (UTF-8)
 *   [2 bytes] PacketNonce length (uint16)
 *   [N bytes] PacketNonce
 *   [4 bytes] Payload length (int32)
 *   [N bytes] Payload
 *   [2 bytes] Signature length (uint16)
 *   [N bytes] Signature
 *
 * Wire-format compatible with C# implementation.
 */
object PacketSerializer {
    /**
     * Serializes a [MeshPacket] to its binary wire format.
     */
    fun serialize(packet: MeshPacket): ByteArray {
        val sourceBytes = packet.sourceUhid.toByteArray(Charsets.UTF_8)
        val destBytes = packet.destinationUhid.toByteArray(Charsets.UTF_8)

        // Calculate total size
        val totalSize = 1 +  // protocol version
                1 +  // packet type
                16 +  // uuid
                1 +  // priority
                4 +  // ttl
                8 +  // timestamp
                2 + sourceBytes.size +
                2 + destBytes.size +
                2 + packet.packetNonce.size +
                4 + packet.payload.size +
                2 + packet.signature.size

        val buffer = ByteBuffer.allocate(totalSize).apply {
            order(ByteOrder.LITTLE_ENDIAN)
        }

        // Protocol version
        buffer.put(packet.protocolVersion)

        // Packet type
        buffer.put(packet.type.value)

        // Packet ID (UUID as bytes)
        val uuidBytes = uuidToBytes(packet.id)
        buffer.put(uuidBytes)

        // Priority
        buffer.put(packet.priority)

        // TTL
        buffer.putInt(packet.ttl)

        // TimestampMs
        buffer.putLong(packet.timestampMs)

        // SourceUhid
        buffer.putShort(sourceBytes.size.toShort())
        buffer.put(sourceBytes)

        // DestinationUhid
        buffer.putShort(destBytes.size.toShort())
        buffer.put(destBytes)

        // PacketNonce
        buffer.putShort(packet.packetNonce.size.toShort())
        buffer.put(packet.packetNonce)

        // Payload
        buffer.putInt(packet.payload.size)
        buffer.put(packet.payload)

        // Signature
        buffer.putShort(packet.signature.size.toShort())
        buffer.put(packet.signature)

        return buffer.array()
    }

    /**
     * Deserializes a [MeshPacket] from its binary wire format.
     */
    fun deserialize(data: ByteArray): MeshPacket {
        if (data.size < 43) {
            throw IllegalArgumentException("Data is too short to contain a valid MeshPacket.")
        }

        val buffer = ByteBuffer.wrap(data).apply {
            order(ByteOrder.LITTLE_ENDIAN)
        }

        // Protocol version
        val protocolVersion = buffer.get()

        // Packet type
        val typeValue = buffer.get()
        val type = PacketType.fromValue(typeValue)
            ?: throw IllegalArgumentException("Unknown packet type: $typeValue")

        // Packet ID
        val idBytes = ByteArray(16)
        buffer.get(idBytes)
        val id = bytesToUUID(idBytes)

        // Priority
        val priority = buffer.get()

        // TTL
        val ttl = buffer.int

        // TimestampMs
        val timestampMs = buffer.long

        // SourceUhid
        val sourceLen = buffer.short.toInt() and 0xFFFF
        ensureRemaining(buffer, sourceLen)
        val sourceBytes = ByteArray(sourceLen)
        buffer.get(sourceBytes)
        val sourceUhid = String(sourceBytes, Charsets.UTF_8)

        // DestinationUhid
        val destLen = buffer.short.toInt() and 0xFFFF
        ensureRemaining(buffer, destLen)
        val destBytes = ByteArray(destLen)
        buffer.get(destBytes)
        val destinationUhid = String(destBytes, Charsets.UTF_8)

        // PacketNonce
        val nonceLen = buffer.short.toInt() and 0xFFFF
        ensureRemaining(buffer, nonceLen)
        val packetNonce = ByteArray(nonceLen)
        buffer.get(packetNonce)

        // Payload
        val payloadLen = buffer.int
        if (payloadLen < 0) {
            throw IllegalArgumentException("Negative payload length.")
        }
        ensureRemaining(buffer, payloadLen)
        val payload = ByteArray(payloadLen)
        buffer.get(payload)

        // Signature
        val sigLen = buffer.short.toInt() and 0xFFFF
        ensureRemaining(buffer, sigLen)
        val signature = ByteArray(sigLen)
        buffer.get(signature)

        return MeshPacket(
            id = id,
            type = type,
            sourceUhid = sourceUhid,
            destinationUhid = destinationUhid,
            ttl = ttl,
            priority = priority,
            payload = payload,
            createdAt = timestampMs,
            signature = signature,
            packetNonce = packetNonce,
            timestampMs = timestampMs,
            protocolVersion = protocolVersion
        )
    }

    /**
     * Attempts to deserialize a packet, returning null on failure instead of throwing.
     */
    fun tryDeserialize(data: ByteArray): MeshPacket? = try {
        deserialize(data)
    } catch (e: Exception) {
        null
    }

    private fun ensureRemaining(buffer: ByteBuffer, required: Int) {
        if (buffer.remaining() < required) {
            throw IllegalArgumentException(
                "Insufficient data: need $required bytes, but only ${buffer.remaining()} remain."
            )
        }
    }

    private fun uuidToBytes(uuid: UUID): ByteArray {
        val buffer = ByteBuffer.allocate(16).apply {
            order(ByteOrder.BIG_ENDIAN)
        }
        buffer.putLong(uuid.mostSignificantBits)
        buffer.putLong(uuid.leastSignificantBits)
        return buffer.array()
    }

    private fun bytesToUUID(bytes: ByteArray): UUID {
        if (bytes.size != 16) {
            throw IllegalArgumentException("UUID bytes must be exactly 16 bytes")
        }
        val buffer = ByteBuffer.wrap(bytes).apply {
            order(ByteOrder.BIG_ENDIAN)
        }
        val msb = buffer.long
        val lsb = buffer.long
        return UUID(msb, lsb)
    }
}
