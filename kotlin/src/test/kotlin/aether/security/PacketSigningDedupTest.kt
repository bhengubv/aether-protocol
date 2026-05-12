// SPDX-License-Identifier: MIT
package aether.security

import aether.protocol.MeshPacket
import aether.protocol.PacketType
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Tests for the nonce dedup fix in [PacketSigning] — pre-2026-05-05 the
 * cache was keyed by `Pair<String, ByteArray>`, which never collided
 * because `ByteArray.equals/hashCode` are identity-based. The fix re-keys
 * by `"<source>:<hex(nonce)>"` to match the C# reference.
 *
 * The second half of the file covers the reputation hooks added in
 * Item 21: replay attempts and signature failures must be forwarded to
 * the optional [NodeReputationService] when one is installed.
 */
class PacketSigningDedupTest {

    // ── helpers ───────────────────────────────────────────────────────────

    /** Reset both mutable singletons between tests. */
    private fun resetPacketSigning() {
        PacketSigning.clearDedupCacheForTests()
        PacketSigning.clearReputationServiceForTests()
    }

    // ── original dedup tests (unchanged) ─────────────────────────────────

    @Test
    fun firstPacket_acceptedReturnsTrue() {
        resetPacketSigning()
        val pkt = newPacket("alice", byteArrayOf(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08))
        assertTrue(PacketSigning.isNewPacket(pkt))
    }

    @Test
    fun replayWithSameNonce_rejected() {
        resetPacketSigning()
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
        resetPacketSigning()
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
        resetPacketSigning()
        val nonce = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
        val fromAlice = newPacket("alice", nonce)
        val fromBob = newPacket("bob", nonce)
        assertTrue(PacketSigning.isNewPacket(fromAlice))
        assertTrue(PacketSigning.isNewPacket(fromBob), "different source MUST not collide")
    }

    @Test
    fun differentNonceFromSameSource_acceptedSeparately() {
        resetPacketSigning()
        val a = newPacket("alice", byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8))
        val b = newPacket("alice", byteArrayOf(9, 10, 11, 12, 13, 14, 15, 16))
        assertTrue(PacketSigning.isNewPacket(a))
        assertTrue(PacketSigning.isNewPacket(b))
    }

    // ── Item 21: reputation hook tests ────────────────────────────────────
    //
    // NodeReputationService is a final class, so we assert on observable
    // score changes rather than subclassing it.  Known deltas:
    //   recordReplayAttempt    → −0.15  (DELTA_REPLAY)
    //   recordSignatureFailure → −0.20  (DELTA_SIG_FAILURE)
    // Unknown peers start at 1.0 so the expected post-hook score is exact.

    /**
     * When a replay is detected, [NodeReputationService.recordReplayAttempt]
     * must be called with the offending source UHID, reducing its score by
     * the replay delta (−0.15).
     */
    @Test
    fun replayDetected_firesRecordReplayAttempt() {
        resetPacketSigning()
        val rep = NodeReputationService()
        PacketSigning.reputation = rep

        val nonce = byteArrayOf(0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80.toByte())
        PacketSigning.isNewPacket(newPacket("mallory", nonce))   // first — accepted
        PacketSigning.isNewPacket(newPacket("mallory", nonce))   // replay

        // Score must have dropped by exactly one DELTA_REPLAY (−0.15)
        assertEquals(0.85, rep.getReputationScore("mallory"), 1e-9,
            "replay hook must down-score the offending peer by 0.15")
    }

    /**
     * A packet with a new (unseen) nonce must NOT alter the peer's
     * reputation score — [recordReplayAttempt] must not be called.
     */
    @Test
    fun newNonce_doesNotFireReplayHook() {
        resetPacketSigning()
        val rep = NodeReputationService()
        PacketSigning.reputation = rep

        PacketSigning.isNewPacket(newPacket("alice", byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)))

        // Score must remain at default 1.0 — no hook was fired
        assertEquals(1.0, rep.getReputationScore("alice"), 1e-9,
            "fresh nonce must not trigger replay hook")
    }

    /**
     * When a packet's signature fails verification,
     * [NodeReputationService.recordSignatureFailure] must be called,
     * reducing the peer's score by the signature-failure delta (−0.20).
     * The return value of [PacketSigning.verifyPacket] must still be false.
     */
    @Test
    fun signatureFailure_firesRecordSignatureFailure() {
        resetPacketSigning()
        val rep = NodeReputationService()
        PacketSigning.reputation = rep

        // Produce a packet signed with priv1 but verified against pub2 —
        // Ed25519 verification will return false (wrong key pair).
        val (priv1, _) = Ed25519Service.generateKeyPair()
        val (_, pub2) = Ed25519Service.generateKeyPair()

        val pkt = newPacket("eve", byteArrayOf(0xAA.toByte(), 0xBB.toByte(),
            0xCC.toByte(), 0xDD.toByte(), 0xEE.toByte(), 0xFF.toByte(), 0x01, 0x02))
        val signedPkt = pkt.copy(signature = PacketSigning.signPacket(pkt, priv1))

        val result = PacketSigning.verifyPacket(signedPkt, pub2)

        assertFalse(result, "mismatched key must fail verification")
        assertEquals(0.80, rep.getReputationScore("eve"), 1e-9,
            "signature failure hook must down-score the peer by 0.20")
    }

    /**
     * When [PacketSigning.reputation] is null, replay detection must complete
     * without throwing an NPE and must still return the correct boolean.
     */
    @Test
    fun nullReputation_noNpe_onReplay() {
        resetPacketSigning()
        // reputation is already null after reset; assignment is explicit for clarity
        PacketSigning.reputation = null

        val nonce = byteArrayOf(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08)
        assertTrue(PacketSigning.isNewPacket(newPacket("alice", nonce)), "first packet accepted")
        // Must not throw even though reputation is null
        assertFalse(PacketSigning.isNewPacket(newPacket("alice", nonce)),
            "replay still rejected with null reputation")
    }

    // ── factory ───────────────────────────────────────────────────────────

    private fun newPacket(source: String, nonce: ByteArray): MeshPacket =
        MeshPacket(
            type = PacketType.Data,
            sourceUhid = source,
            destinationUhid = "bob",
            packetNonce = nonce,
            payload = "x".toByteArray(),
        )
}
