// SPDX-License-Identifier: MIT
package aether.security

import aether.protocol.MeshPacket
import aether.protocol.PacketType
import org.junit.jupiter.api.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Tests for the nonce dedup fix in [PacketSigning] — pre-2026-05-05 the
 * cache was keyed by `Pair<String, ByteArray>`, which never collided
 * because `ByteArray.equals/hashCode` are identity-based. The fix re-keys
 * by `"<source>:<hex(nonce)>"` to match the C# reference.
 */
class PacketSigningDedupTest {

    @Test
    fun firstPacket_acceptedReturnsTrue() {
        PacketSigning.clearDedupCacheForTests()
        val pkt = newPacket("alice", byteArrayOf(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08))
        assertTrue(PacketSigning.isNewPacket(pkt))
    }

    @Test
    fun replayWithSameNonce_rejected() {
        PacketSigning.clearDedupCacheForTests()
        val nonce = byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte(), 0xDD.toByte(),
            0xEE.toByte(), 0xFF.toByte(), 0x11, 0x22)
        val first = newPacket("alice", nonce)
        val second = newPacket("alice", nonce)
        assertTrue(PacketSigning.isNewPacket(first))
        assertFalse(PacketSigning.isNewPacket(second), "replay must be rejected")
    }

    @Test
    fun replayWithSameNonceBytesDifferentArrayInstance_stillRejected() {
        // Pre-fix this passed because ByteArray uses identity equals.
        // Post-fix the hex-key lookup correctly identifies the replay.
        PacketSigning.clearDedupCacheForTests()
        val first = newPacket("alice", byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8))
        val secondCopy = newPacket("alice", byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8))
        assertTrue(PacketSigning.isNewPacket(first))
        assertFalse(
            PacketSigning.isNewPacket(secondCopy),
            "value-equal nonces from different ByteArray instances must collide",
        )
    }

    @Test
    fun sameNonceFromDifferentSource_acceptedSeparately() {
        // Cross-source nonce reuse must NOT cause a drop — the (source, nonce)
        // tuple is what we dedup on.
        PacketSigning.clearDedupCacheForTests()
        val nonce = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
        val fromAlice = newPacket("alice", nonce)
        val fromBob = newPacket("bob", nonce)
        assertTrue(PacketSigning.isNewPacket(fromAlice))
        assertTrue(PacketSigning.isNewPacket(fromBob), "different source MUST not collide")
    }

    @Test
    fun differentNonceFromSameSource_acceptedSeparately() {
        PacketSigning.clearDedupCacheForTests()
        val a = newPacket("alice", byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8))
        val b = newPacket("alice", byteArrayOf(9, 10, 11, 12, 13, 14, 15, 16))
        assertTrue(PacketSigning.isNewPacket(a))
        assertTrue(PacketSigning.isNewPacket(b))
    }

    private fun newPacket(source: String, nonce: ByteArray): MeshPacket =
        MeshPacket(
            type = PacketType.Data,
            sourceUhid = source,
            destinationUhid = "bob",
            packetNonce = nonce,
            payload = "x".toByteArray(),
        )
}
