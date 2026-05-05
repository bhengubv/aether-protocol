// SPDX-License-Identifier: MIT
package aether.protocol

import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFails
import kotlin.test.assertNull

/**
 * Round-trip tests for [PacketSerializer]. Mirror of
 * `swift/Tests/PacketSerializationTests.swift`. Cross-language byte equivalence
 * is anchored separately under `fixtures/`.
 */
class PacketSerializerTest {

    private fun nonce(fill: Byte = 0): ByteArray = ByteArray(8) { fill }

    @Test fun roundTrip_preservesAllFields() {
        val p = MeshPacket(
            type = PacketType.Data,
            sourceUhid = "alice-node",
            destinationUhid = "bob-node",
            ttl = 7,
            priority = 10,
            payload = "Hello, Aether!".toByteArray(Charsets.UTF_8),
            packetNonce = nonce(0xAB.toByte()),
            timestampMs = 1_710_528_000_000L,
        )
        val bytes = PacketSerializer.serialize(p)
        val got = PacketSerializer.deserialize(bytes)

        assertEquals(p.type, got.type)
        assertEquals(p.sourceUhid, got.sourceUhid)
        assertEquals(p.destinationUhid, got.destinationUhid)
        assertEquals(p.ttl, got.ttl)
        assertEquals(p.priority, got.priority)
        assertContentEquals(p.payload, got.payload)
        assertContentEquals(p.packetNonce, got.packetNonce)
        assertEquals(p.protocolVersion, got.protocolVersion)
    }

    @Test fun emptyDestinationUhid_roundTrips() {
        val p = MeshPacket(
            type = PacketType.SosBroadcast,
            sourceUhid = "node-1",
            destinationUhid = "",
            packetNonce = nonce(),
        )
        val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
        assertEquals("node-1", got.sourceUhid)
        assertEquals("", got.destinationUhid)
    }

    @Test fun emptyPayload_roundTrips() {
        val p = MeshPacket(
            type = PacketType.Heartbeat,
            sourceUhid = "node-1",
            packetNonce = nonce(),
            payload = ByteArray(0),
        )
        val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
        assertEquals(0, got.payload.size)
    }

    @Test fun largePayload_roundTrips() {
        val payload = ByteArray(262_144) { 0xFF.toByte() }
        val p = MeshPacket(
            type = PacketType.ChunkData,
            sourceUhid = "node-1",
            destinationUhid = "node-2",
            packetNonce = nonce(),
            payload = payload,
        )
        val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
        assertEquals(262_144, got.payload.size)
        assertEquals(0xFF.toByte(), got.payload[0])
        assertEquals(0xFF.toByte(), got.payload[262_143])
    }

    @Test fun uuid_roundTrips() {
        val expected = UUID.fromString("550e8400-e29b-41d4-a716-446655440000")
        val p = MeshPacket(
            id = expected,
            type = PacketType.Data,
            sourceUhid = "node-1",
            packetNonce = nonce(),
        )
        val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
        assertEquals(expected, got.id)
    }

    @Test fun uuid_wireOrderIsRfc4122BigEndian() {
        // 16 bytes after [version(1), type(1)] must be UUID in RFC 4122 big-endian.
        val expected = UUID.fromString("550e8400-e29b-41d4-a716-446655440000")
        val p = MeshPacket(
            id = expected,
            type = PacketType.Data,
            sourceUhid = "n",
            packetNonce = nonce(),
        )
        val bytes = PacketSerializer.serialize(p)
        val want = byteArrayOf(
            0x55, 0x0e, 0x84.toByte(), 0x00,
            0xe2.toByte(), 0x9b.toByte(), 0x41, 0xd4.toByte(),
            0xa7.toByte(), 0x16, 0x44, 0x66,
            0x55, 0x44, 0x00, 0x00,
        )
        assertContentEquals(want, bytes.copyOfRange(2, 18))
    }

    @Test fun tooShort_throws() {
        assertFails { PacketSerializer.deserialize(byteArrayOf(0x01, 0x02)) }
    }

    @Test fun tryDeserialize_returnsNullOnGarbage() {
        assertNull(PacketSerializer.tryDeserialize(byteArrayOf(0xFF.toByte())))
    }

    @Test fun allPacketTypes_roundTrip() {
        for (t in PacketType.values()) {
            val p = MeshPacket(
                type = t,
                sourceUhid = "node-${t.value}",
                packetNonce = nonce(),
            )
            val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
            assertEquals(t, got.type)
        }
    }

    @Test fun timestamp_preservedToMs() {
        val p = MeshPacket(
            type = PacketType.Data,
            sourceUhid = "node-1",
            timestampMs = 1_710_528_000_000L,
            packetNonce = nonce(),
        )
        val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
        assertEquals(1_710_528_000_000L, got.timestampMs)
    }

    @Test fun unicodeUhids_roundTrip() {
        val p = MeshPacket(
            type = PacketType.Data,
            sourceUhid = "노드-1",
            destinationUhid = "узел-2",
            packetNonce = nonce(),
        )
        val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
        assertEquals("노드-1", got.sourceUhid)
        assertEquals("узел-2", got.destinationUhid)
    }

    @Test fun signature_preserved() {
        val sig = ByteArray(64) { 0xAB.toByte() }
        val p = MeshPacket(
            type = PacketType.Data,
            sourceUhid = "node-1",
            packetNonce = nonce(),
            signature = sig,
        )
        val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
        assertContentEquals(sig, got.signature)
    }

    @Test fun ttl_fullInt32RangePreserved() {
        // > UInt8 max — would have wrapped to 0 under the pre-2026-05-02 bug.
        val p = MeshPacket(
            type = PacketType.Data,
            sourceUhid = "n",
            ttl = 256,
            packetNonce = nonce(),
        )
        val got = PacketSerializer.deserialize(PacketSerializer.serialize(p))
        assertEquals(256, got.ttl)
    }
}
