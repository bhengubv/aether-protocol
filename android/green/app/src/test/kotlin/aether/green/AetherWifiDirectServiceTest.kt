// SPDX-License-Identifier: MIT

package aether.green

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Unit tests for the pure (no-Android-framework) logic extracted into
 * [AetherWifiDirectService.Companion].
 *
 * Covers wire-format parsing, TTL-decrement echo, and TCP framing helpers.
 * Runs on the JVM — no emulator, no Android framework required.
 */
class AetherWifiDirectServiceTest {

    // ── Helpers ──────────────────────────────────────────────────────────────

    private fun minPacket(
        version:  Byte = 2,
        type:     Byte = 3,
        priority: Byte = 10,
        ttl:      Int  = 7,
        extra:    Int  = 0
    ): ByteArray = ByteArray(31 + extra).also { b ->
        b[0] = version
        b[1] = type
        // bytes 2-17: GUID (all zeros)
        b[18] = priority
        ByteBuffer.wrap(b, 19, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(ttl)
        // bytes 23-30: timestamp (all zeros)
    }

    private fun readTtl(buf: ByteArray): Int =
        ByteBuffer.wrap(buf, 19, 4).order(ByteOrder.LITTLE_ENDIAN).int

    // ── parsePacketSummary — too-short inputs ─────────────────────────────────

    @Test fun summary_emptyBuffer_reportsTooShort() {
        val s = AetherWifiDirectService.parsePacketSummary(ByteArray(0))
        assertTrue("Expected 'too short' in: $s", s.contains("too short"))
    }

    @Test fun summary_30ByteBuffer_reportsTooShort() {
        val s = AetherWifiDirectService.parsePacketSummary(ByteArray(30))
        assertTrue("Expected 'too short' in: $s", s.contains("too short"))
        assertTrue("Expected 30 in: $s", s.contains("30"))
    }

    // ── parsePacketSummary — valid inputs ─────────────────────────────────────

    @Test fun summary_valid31BytePacket_extractsAllFields() {
        val s = AetherWifiDirectService.parsePacketSummary(
            minPacket(version = 2, type = 3, priority = 10, ttl = 7)
        )
        assertTrue(s.contains("v=2"))
        assertTrue(s.contains("type=3"))
        assertTrue(s.contains("pri=10"))
        assertTrue(s.contains("ttl=7"))
        assertTrue(s.contains("total=31B"))
    }

    @Test fun summary_zeroTtl_reportedCorrectly() {
        val s = AetherWifiDirectService.parsePacketSummary(minPacket(ttl = 0))
        assertTrue("Expected ttl=0 in: $s", s.contains("ttl=0"))
    }

    @Test fun summary_maxByteBoundaries_reportedCorrectly() {
        val s = AetherWifiDirectService.parsePacketSummary(
            minPacket(type = 255.toByte(), priority = 255.toByte(), ttl = 255)
        )
        assertTrue(s.contains("type=255"))
        assertTrue(s.contains("pri=255"))
        assertTrue(s.contains("ttl=255"))
    }

    @Test fun summary_largerPacket_reportsTotalByteCount() {
        val s = AetherWifiDirectService.parsePacketSummary(minPacket(extra = 69)) // 100 B
        assertTrue("Expected total=100B in: $s", s.contains("total=100B"))
    }

    // ── buildEchoResponse ─────────────────────────────────────────────────────

    @Test fun echo_returnsCopyNotSameReference() {
        val data = minPacket(ttl = 5)
        assertNotSame(data, AetherWifiDirectService.buildEchoResponse(data))
    }

    @Test fun echo_packetShorterThan24Bytes_returnedUnchanged() {
        val data = ByteArray(20) { it.toByte() }
        assertArrayEquals(data, AetherWifiDirectService.buildEchoResponse(data))
    }

    @Test fun echo_ttl7_decrementsTo6() {
        val echo = AetherWifiDirectService.buildEchoResponse(minPacket(ttl = 7))
        assertEquals(6, readTtl(echo))
    }

    @Test fun echo_ttl1_decrementsTo0() {
        val echo = AetherWifiDirectService.buildEchoResponse(minPacket(ttl = 1))
        assertEquals(0, readTtl(echo))
    }

    @Test fun echo_ttl0_clampedTo0() {
        val echo = AetherWifiDirectService.buildEchoResponse(minPacket(ttl = 0))
        assertEquals("TTL must not go below 0", 0, readTtl(echo))
    }

    @Test fun echo_nonTtlBytesUnchanged() {
        val data = minPacket(version = 2, type = 3, priority = 10, ttl = 5)
        val echo = AetherWifiDirectService.buildEchoResponse(data)
        assertEquals("version unchanged", data[0], echo[0])
        assertEquals("type unchanged",    data[1], echo[1])
        assertEquals("priority unchanged", data[18], echo[18])
    }

    // ── buildFrameHeader ──────────────────────────────────────────────────────

    @Test fun frameHeader_zero_isFourZeroBytes() {
        val h = AetherWifiDirectService.buildFrameHeader(0)
        assertEquals(4, h.size)
        assertArrayEquals(byteArrayOf(0, 0, 0, 0), h)
    }

    @Test fun frameHeader_1_isLittleEndian() {
        val h = AetherWifiDirectService.buildFrameHeader(1)
        assertEquals(4, h.size)
        // Little-endian: 0x00000001 → [0x01, 0x00, 0x00, 0x00]
        assertArrayEquals(byteArrayOf(0x01, 0x00, 0x00, 0x00), h)
    }

    @Test fun frameHeader_256_isLittleEndian() {
        val h = AetherWifiDirectService.buildFrameHeader(256)
        // Little-endian: 0x00000100 → [0x00, 0x01, 0x00, 0x00]
        assertArrayEquals(byteArrayOf(0x00, 0x01, 0x00, 0x00), h)
    }

    @Test fun frameHeader_65536_isLittleEndian() {
        val h = AetherWifiDirectService.buildFrameHeader(65536)
        // Little-endian: 0x00010000 → [0x00, 0x00, 0x01, 0x00]
        assertArrayEquals(byteArrayOf(0x00, 0x00, 0x01, 0x00), h)
    }

    // ── parseFrameLength ──────────────────────────────────────────────────────

    @Test fun parseFrameLength_roundTrips_withBuildFrameHeader() {
        listOf(0, 1, 255, 256, 65535, 65536, 1_000_000).forEach { len ->
            val header = AetherWifiDirectService.buildFrameHeader(len)
            val parsed = AetherWifiDirectService.parseFrameLength(header)
            assertEquals("Round-trip failed for len=$len", len, parsed)
        }
    }

    @Test fun parseFrameLength_wrongSizeHeader_returnsNegativeOne() {
        assertEquals(-1, AetherWifiDirectService.parseFrameLength(ByteArray(0)))
        assertEquals(-1, AetherWifiDirectService.parseFrameLength(ByteArray(3)))
        assertEquals(-1, AetherWifiDirectService.parseFrameLength(ByteArray(5)))
    }

    @Test fun parseFrameLength_allZeroHeader_returnsZero() {
        assertEquals(0, AetherWifiDirectService.parseFrameLength(ByteArray(4)))
    }
}
