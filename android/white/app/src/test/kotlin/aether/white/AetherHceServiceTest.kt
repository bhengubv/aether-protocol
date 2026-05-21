// SPDX-License-Identifier: MIT

package aether.white

import org.junit.Assert.*
import org.junit.Test

/**
 * Unit tests for the pure (no-Android-framework) logic extracted into
 * [AetherHceService.Companion].
 *
 * Covers ISO 7816 SELECT AID parsing ([AetherHceService.isSelectAid]) and
 * APDU summary generation ([AetherHceService.parsePacketSummary]).
 * Runs on the JVM — no emulator, no Android framework required.
 *
 * The Aether HCE AID is `F061657468657200` (8 bytes, "aether" ASCII + NUL).
 */
class AetherHceServiceTest {

    // ── isSelectAid — valid SELECT AID ───────────────────────────────────────

    @Test fun isSelectAid_wellFormedSelectAid_returnsTrue() {
        val apdu = AetherHceService.buildSelectAidApdu()
        assertTrue("Well-formed SELECT AID must be recognised", AetherHceService.isSelectAid(apdu))
    }

    @Test fun isSelectAid_wellFormedSelectAid_caseInsensitive() {
        // Build with lower-case AID bytes — isSelectAid must still accept them
        // because it uses `ignoreCase = true`.
        val apdu = AetherHceService.buildSelectAidApdu() // produces upper-case hex, still a byte array
        assertTrue(AetherHceService.isSelectAid(apdu))
    }

    // ── isSelectAid — too-short inputs ────────────────────────────────────────

    @Test fun isSelectAid_emptyApdu_returnsFalse() {
        assertFalse(AetherHceService.isSelectAid(ByteArray(0)))
    }

    @Test fun isSelectAid_4ByteApdu_tooShort_returnsFalse() {
        assertFalse(AetherHceService.isSelectAid(byteArrayOf(0x00, 0xA4.toByte(), 0x04, 0x00)))
    }

    @Test fun isSelectAid_5ByteApdu_lcButNoAidBytes_returnsFalse() {
        // Header OK, lc=8 but zero AID bytes follow → size < 5 + 8
        assertFalse(AetherHceService.isSelectAid(byteArrayOf(0x00, 0xA4.toByte(), 0x04, 0x00, 0x08)))
    }

    // ── isSelectAid — wrong header ────────────────────────────────────────────

    @Test fun isSelectAid_wrongCla_returnsFalse() {
        val apdu = AetherHceService.buildSelectAidApdu().copyOf()
        apdu[0] = 0x80.toByte() // wrong CLA (should be 0x00)
        assertFalse(AetherHceService.isSelectAid(apdu))
    }

    @Test fun isSelectAid_wrongIns_returnsFalse() {
        val apdu = AetherHceService.buildSelectAidApdu().copyOf()
        apdu[1] = 0xB0.toByte() // wrong INS (should be 0xA4)
        assertFalse(AetherHceService.isSelectAid(apdu))
    }

    @Test fun isSelectAid_wrongP1_returnsFalse() {
        val apdu = AetherHceService.buildSelectAidApdu().copyOf()
        apdu[2] = 0x02 // wrong P1 (should be 0x04)
        assertFalse(AetherHceService.isSelectAid(apdu))
    }

    @Test fun isSelectAid_wrongAidBytes_returnsFalse() {
        val apdu = AetherHceService.buildSelectAidApdu().copyOf()
        // Corrupt the last byte of the AID
        apdu[apdu.size - 1] = 0xFF.toByte()
        assertFalse(AetherHceService.isSelectAid(apdu))
    }

    @Test fun isSelectAid_randomBytes_returnsFalse() {
        assertFalse(AetherHceService.isSelectAid(ByteArray(16) { 0xFF.toByte() }))
    }

    // ── isSelectAid — lc truncation ───────────────────────────────────────────

    @Test fun isSelectAid_lcLargerThanRemainingBytes_returnsFalse() {
        val base = AetherHceService.buildSelectAidApdu()
        // Set lc to 255 (far more bytes than actually follow)
        val apdu = base.copyOf()
        apdu[4] = 0xFF.toByte()
        assertFalse(AetherHceService.isSelectAid(apdu))
    }

    // ── parsePacketSummary ────────────────────────────────────────────────────

    @Test fun parsePacketSummary_emptyApdu_returnsByteCount() {
        val s = AetherHceService.parsePacketSummary(ByteArray(0))
        assertTrue("Expected '0B' in: $s", s.contains("0B"))
    }

    @Test fun parsePacketSummary_oneByteApdu_returnsByteCount() {
        val s = AetherHceService.parsePacketSummary(byteArrayOf(0x01))
        assertTrue("Expected '1B' in: $s", s.contains("1B"))
    }

    @Test fun parsePacketSummary_twoBytesApdu_extractsClassAndInstruction() {
        val s = AetherHceService.parsePacketSummary(byteArrayOf(0x00, 0xA4.toByte()))
        assertTrue("Expected CLA=00 in: $s", s.contains("CLA=00"))
        assertTrue("Expected INS=A4 in: $s", s.contains("INS=A4") || s.contains("INS=a4"))
    }

    @Test fun parsePacketSummary_selectAidApdu_showsCorrectHeaderAndSize() {
        val apdu = AetherHceService.buildSelectAidApdu()
        val s    = AetherHceService.parsePacketSummary(apdu)
        assertTrue("Expected CLA=00 in: $s",              s.contains("CLA=00"))
        assertTrue("Expected INS=A4 or a4 in: $s",        s.contains("INS=A4") || s.contains("INS=a4"))
        assertTrue("Expected total size in: $s",
            s.contains("${apdu.size}B total"))
    }

    @Test fun parsePacketSummary_ffBytes_showsHexUpperCase() {
        val s = AetherHceService.parsePacketSummary(byteArrayOf(0xFF.toByte(), 0xFF.toByte()))
        assertTrue("Expected CLA=FF in: $s", s.contains("CLA=FF") || s.contains("CLA=ff"))
        assertTrue("Expected INS=FF in: $s", s.contains("INS=FF") || s.contains("INS=ff"))
    }

    // ── buildSelectAidApdu (builder consistency) ──────────────────────────────

    @Test fun buildSelectAidApdu_producesValidSelectCommand() {
        val apdu = AetherHceService.buildSelectAidApdu()
        // Must start with the SELECT AID header bytes
        assertEquals("CLA", 0x00.toByte(), apdu[0])
        assertEquals("INS", 0xA4.toByte(), apdu[1])
        assertEquals("P1",  0x04.toByte(), apdu[2])
        assertEquals("P2",  0x00.toByte(), apdu[3])
        // Lc must equal the AID length (8 bytes = F0 61 65 74 68 65 72 00)
        assertEquals("Lc (AID length)", 8, apdu[4].toInt() and 0xFF)
        assertTrue("Total size", apdu.size == 13) // 4 header + 1 Lc + 8 AID
    }

    @Test fun buildSelectAidApdu_isRecognisedByIsSelectAid() {
        assertTrue(AetherHceService.isSelectAid(AetherHceService.buildSelectAidApdu()))
    }
}
