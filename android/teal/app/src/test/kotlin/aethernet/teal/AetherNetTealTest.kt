// SPDX-License-Identifier: MIT

package aethernet.teal

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Unit tests for the pure (no-Android-framework) logic in [AetherNetSleService]:
 *   - [AetherNetSleService.parsePacketSummary] — Aether wire-format header parsing.
 *   - [AetherNetSleService.Framer] — SSAP-over-BLE fragmentation/reassembly, which is
 *     wire-identical to BleGattFramer.cs so the Windows central and this Android peripheral
 *     interoperate byte-for-byte.
 *
 * These run on the JVM via Gradle's `test` task — no emulator needed.
 */
class AetherNetTealTest {

    // Sanity (retained from the original placeholder).
    @Test fun packageName_hasAetherNetPrefix() = assertTrue("aethernet.teal".startsWith("aether"))

    // ── Helpers ──────────────────────────────────────────────────────────────

    private fun minPacket(
        version: Byte = 2, type: Byte = 3, priority: Byte = 10, ttl: Int = 7, extra: Int = 0
    ): ByteArray = ByteArray(31 + extra).also { b ->
        b[0] = version
        b[1] = type
        b[18] = priority
        ByteBuffer.wrap(b, 19, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(ttl)
    }

    // ── parsePacketSummary ────────────────────────────────────────────────────

    @Test fun summary_valid31Byte_extractsAllFields() {
        val s = AetherNetSleService.parsePacketSummary(minPacket(version = 2, type = 3, priority = 10, ttl = 7))
        assertTrue(s, s.contains("v=2✓"))
        assertTrue(s, s.contains("type=3"))
        assertTrue(s, s.contains("pri=10"))
        assertTrue(s, s.contains("ttl=7"))
        assertTrue(s, s.contains("total=31B"))
    }

    @Test fun summary_version1_showsQuestionMark() =
        assertTrue(AetherNetSleService.parsePacketSummary(minPacket(version = 1)).contains("v=1?"))

    @Test fun summary_tooShort_reported() {
        val s = AetherNetSleService.parsePacketSummary(ByteArray(30))
        assertTrue(s, s.contains("too short"))
        assertTrue(s, s.contains("30"))
    }

    @Test fun summary_largeTtl_reported() =
        assertTrue(AetherNetSleService.parsePacketSummary(minPacket(ttl = 1_000_000)).contains("ttl=1000000"))

    // ── Framer.frame — header correctness ──────────────────────────────────────

    @Test fun frame_emptyData_singleFrameWithHeaderOnly() {
        val frames = AetherNetSleService.Framer.frame(ByteArray(0))
        assertEquals(1, frames.size)
        assertEquals(4, frames[0].size)          // header only, no payload
        assertEquals(1, frameCount(frames[0]))   // frame_count == 1
        assertEquals(0, frameIndex(frames[0]))   // frame_index == 0
    }

    @Test fun frame_smallData_singleFrame() {
        val data = byteArrayOf(1, 2, 3, 4, 5)
        val frames = AetherNetSleService.Framer.frame(data)   // default mtu 1024
        assertEquals(1, frames.size)
        assertEquals(4 + 5, frames[0].size)
        assertEquals(1, frameCount(frames[0]))
    }

    @Test fun frame_largeData_splitsIntoOrderedFrames() {
        val data = ByteArray(10) { it.toByte() }
        val frames = AetherNetSleService.Framer.frame(data, mtu = 8) // maxPayload = 4 → 3 frames
        assertEquals(3, frames.size)
        frames.forEachIndexed { i, f ->
            assertEquals("frame_count", 3, frameCount(f))
            assertEquals("frame_index $i", i, frameIndex(f))
        }
        // Payloads: 4 + 4 + 2 = 10
        assertEquals(4 + 4, frames[0].size)
        assertEquals(4 + 4, frames[1].size)
        assertEquals(4 + 2, frames[2].size)
    }

    // ── Framer.Reassembler — round-trip ─────────────────────────────────────────

    @Test fun roundTrip_singleFrame() {
        val data = minPacket(ttl = 42)
        val r = AetherNetSleService.Framer.Reassembler()
        var out: ByteArray? = null
        for (f in AetherNetSleService.Framer.frame(data)) out = r.accumulate(f)
        assertArrayEquals(data, out)
    }

    @Test fun roundTrip_multiFrame_reassemblesExactly() {
        val data = ByteArray(2500) { (it % 256).toByte() } // > default 1020 payload → 3 frames
        val frames = AetherNetSleService.Framer.frame(data)
        assertEquals(3, frames.size)

        val r = AetherNetSleService.Framer.Reassembler()
        var out: ByteArray? = null
        for ((i, f) in frames.withIndex()) {
            val partial = r.accumulate(f)
            if (i < frames.size - 1) assertNull("Incomplete sequence must yield null", partial)
            else out = partial
        }
        assertArrayEquals(data, out)
    }

    @Test fun reassembler_incompleteSequence_returnsNull() {
        val data = ByteArray(10) { it.toByte() }
        val frames = AetherNetSleService.Framer.frame(data, mtu = 8) // 3 frames
        val r = AetherNetSleService.Framer.Reassembler()
        assertNull(r.accumulate(frames[0]))
        assertNull(r.accumulate(frames[1]))
        assertArrayEquals(data, r.accumulate(frames[2]))
    }

    @Test fun reassembler_newIndexZero_resetsBuffer() {
        val r = AetherNetSleService.Framer.Reassembler()
        // Feed a partial message, then a brand-new single-frame message (index 0) must reset.
        val big = AetherNetSleService.Framer.frame(ByteArray(10) { it.toByte() }, mtu = 8)
        r.accumulate(big[0]) // partial start of a 3-frame message
        val small = AetherNetSleService.Framer.frame(byteArrayOf(9, 9, 9))
        assertArrayEquals(byteArrayOf(9, 9, 9), r.accumulate(small[0]))
    }

    // ── header readers (little-endian uint16) ──────────────────────────────────

    private fun frameCount(f: ByteArray): Int = (f[0].toInt() and 0xFF) or ((f[1].toInt() and 0xFF) shl 8)
    private fun frameIndex(f: ByteArray): Int = (f[2].toInt() and 0xFF) or ((f[3].toInt() and 0xFF) shl 8)
}
