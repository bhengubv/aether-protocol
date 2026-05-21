// SPDX-License-Identifier: MIT

package aether.blue

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Unit tests for the pure (no-Android-framework) logic extracted into
 * [AetherGattServer.Companion].
 *
 * These tests run on the JVM via Gradle's `test` task — no emulator needed.
 * They cover [AetherGattServer.parsePacketSummary] (wire-format header parsing)
 * and [AetherGattServer.buildEchoResponse] (TTL-decrement echo logic), which
 * are the correctness-critical paths in the BLE GATT transport.
 */
class AetherGattServerTest {

    // ── Helpers ──────────────────────────────────────────────────────────────

    /** Builds a 31-byte Aether fixed header with the supplied field values. */
    private fun minPacket(
        version:  Byte = 2,
        type:     Byte = 3,
        priority: Byte = 10,
        ttl:      Int  = 7,
        extra:    Int  = 0  // additional zero-padding bytes beyond 31
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
        val s = AetherGattServer.parsePacketSummary(ByteArray(0))
        assertTrue("Expected 'too short' in: $s", s.contains("too short"))
        assertTrue("Expected byte count 0 in: $s", s.contains("0"))
    }

    @Test fun summary_oneByteBuffer_reportsTooShort() {
        val s = AetherGattServer.parsePacketSummary(ByteArray(1))
        assertTrue("Expected 'too short' in: $s", s.contains("too short"))
    }

    @Test fun summary_30ByteBuffer_reportsTooShort() {
        val s = AetherGattServer.parsePacketSummary(ByteArray(30))
        assertTrue("Expected 'too short' in: $s", s.contains("too short"))
        assertTrue("Expected byte count 30 in: $s", s.contains("30"))
    }

    // ── parsePacketSummary — valid inputs ─────────────────────────────────────

    @Test fun summary_valid31BytePacket_extractsAllFields() {
        val s = AetherGattServer.parsePacketSummary(minPacket(version = 2, type = 3, priority = 10, ttl = 7))
        assertTrue("Expected v=2✓ in: $s",   s.contains("v=2✓"))
        assertTrue("Expected type=3 in: $s", s.contains("type=3"))
        assertTrue("Expected pri=10 in: $s", s.contains("pri=10"))
        assertTrue("Expected ttl=7 in: $s",  s.contains("ttl=7"))
        assertTrue("Expected total=31B in: $s", s.contains("total=31B"))
    }

    @Test fun summary_version2_showsCheckmark() {
        val s = AetherGattServer.parsePacketSummary(minPacket(version = 2))
        assertTrue("Version 2 must show ✓: $s", s.contains("v=2✓"))
    }

    @Test fun summary_version1_showsQuestionMark() {
        val s = AetherGattServer.parsePacketSummary(minPacket(version = 1))
        assertTrue("Non-2 version must show ?: $s", s.contains("v=1?"))
    }

    @Test fun summary_version0_showsQuestionMark() {
        val s = AetherGattServer.parsePacketSummary(minPacket(version = 0))
        assertTrue("Version 0 must show ?: $s", s.contains("v=0?"))
    }

    @Test fun summary_zeroTtl_reportedCorrectly() {
        val s = AetherGattServer.parsePacketSummary(minPacket(ttl = 0))
        assertTrue("Expected ttl=0 in: $s", s.contains("ttl=0"))
    }

    @Test fun summary_maxPriorityAndType_reportedCorrectly() {
        val s = AetherGattServer.parsePacketSummary(
            minPacket(type = 255.toByte(), priority = 255.toByte(), ttl = 15)
        )
        assertTrue("Expected type=255 in: $s",  s.contains("type=255"))
        assertTrue("Expected pri=255 in: $s",   s.contains("pri=255"))
        assertTrue("Expected ttl=15 in: $s",    s.contains("ttl=15"))
    }

    @Test fun summary_largeTtl_reportedCorrectly() {
        val s = AetherGattServer.parsePacketSummary(minPacket(ttl = 1_000_000))
        assertTrue("Expected large ttl in: $s", s.contains("ttl=1000000"))
    }

    @Test fun summary_longerPacket_reportsTotalByteCount() {
        val s = AetherGattServer.parsePacketSummary(minPacket(extra = 69)) // 31 + 69 = 100 B
        assertTrue("Expected total=100B in: $s", s.contains("total=100B"))
    }

    // ── buildEchoResponse — copy semantics ────────────────────────────────────

    @Test fun echo_returnsCopyNotSameReference() {
        val data = minPacket(ttl = 5)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertNotSame("Must return a copy, not the same array", data, echo)
    }

    @Test fun echo_copiesAllBytesExactly() {
        val data = minPacket(ttl = 5)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertEquals("Echo must be same length as input", data.size, echo.size)
    }

    // ── buildEchoResponse — short packets (< 24 B) ───────────────────────────

    @Test fun echo_packetShorterThan24Bytes_returnedUnchanged() {
        val data = ByteArray(23) { it.toByte() }
        val echo = AetherGattServer.buildEchoResponse(data)
        assertArrayEquals("Packet < 24 B must be returned unchanged", data, echo)
    }

    @Test fun echo_emptyPacket_returnedUnchanged() {
        val data = ByteArray(0)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertArrayEquals("Empty packet must be returned as empty copy", data, echo)
    }

    // ── buildEchoResponse — TTL decrement ─────────────────────────────────────

    @Test fun echo_ttl7_decrementsTo6() {
        val data = minPacket(ttl = 7)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertEquals(6, readTtl(echo))
    }

    @Test fun echo_ttl1_decrementsTo0() {
        val data = minPacket(ttl = 1)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertEquals(0, readTtl(echo))
    }

    @Test fun echo_ttl0_staysAt0_notNegative() {
        val data = minPacket(ttl = 0)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertEquals("TTL must be clamped to 0", 0, readTtl(echo))
    }

    @Test fun echo_ttlMaxValue_decrementsBy1() {
        val data = minPacket(ttl = Int.MAX_VALUE)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertEquals(Int.MAX_VALUE - 1, readTtl(echo))
    }

    // ── buildEchoResponse — field isolation ───────────────────────────────────

    @Test fun echo_versionByteUnchanged() {
        val data = minPacket(version = 2, ttl = 5)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertEquals("Version byte must not be modified", data[0], echo[0])
    }

    @Test fun echo_typeByteUnchanged() {
        val data = minPacket(type = 3, ttl = 5)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertEquals("Type byte must not be modified", data[1], echo[1])
    }

    @Test fun echo_priorityByteUnchanged() {
        val data = minPacket(priority = 10, ttl = 5)
        val echo = AetherGattServer.buildEchoResponse(data)
        assertEquals("Priority byte must not be modified", data[18], echo[18])
    }

    @Test fun echo_guidBytesUnchanged() {
        val data = minPacket(ttl = 5).also { b ->
            // Write distinct non-zero GUID bytes so we can verify them
            for (i in 2..17) b[i] = (i * 7).toByte()
        }
        val echo = AetherGattServer.buildEchoResponse(data)
        for (i in 2..17) {
            assertEquals("GUID byte $i must not be modified", data[i], echo[i])
        }
    }
}
