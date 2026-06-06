// SPDX-License-Identifier: MIT
package aethermesh.transport

import aethermesh.transport.rlnc.RlncCodec
import aethermesh.transport.rlnc.RlncDecoder
import aethermesh.transport.rlnc.RlncEncoder
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

// ── Helpers ────────────────────────────────────────────────────────────────────

private fun splitPackets(buf: ByteArray, count: Int): List<ByteArray> {
    val pktSize = buf.size / count
    return (0 until count).map { i -> buf.copyOfRange(i * pktSize, (i + 1) * pktSize) }
}

// ── RlncCodec round-trips ──────────────────────────────────────────────────────

class RlncCodecTest {

    @Test fun `k4 systematic round-trip`() {
        val source  = "aether-rlnc-kotlin-test".toByteArray(Charsets.UTF_8)
        val codec   = RlncCodec(generationSize = 4)
        val encoded = codec.encode(source, 4)
        val pkts    = splitPackets(encoded, 4)
        val decoded = codec.tryDecode(pkts, 4)
        assertNotNull(decoded)
        assertEquals(
            source.toList(),
            decoded.take(source.size).toList(),
            "decoded payload mismatch"
        )
    }

    @Test fun `k4 repair-only round-trip`() {
        val source  = "repair-only Kotlin RLNC test".toByteArray()
        val codec   = RlncCodec(generationSize = 4)
        val encoded = codec.encode(source, 8) // 4 systematic + 4 repair
        val pkts    = splitPackets(encoded, 8).drop(4) // skip systematic
        val decoded = codec.tryDecode(pkts, 4)
        assertNotNull(decoded, "repair-only decode returned null")
        assertEquals(source.toList(), decoded.take(source.size).toList())
    }

    @Test fun `k1 single-symbol round-trip`() {
        val source  = byteArrayOf('z'.code.toByte())
        val codec   = RlncCodec(generationSize = 1)
        val encoded = codec.encode(source, 2)
        val pkts    = splitPackets(encoded, 2).take(1)
        val decoded = codec.tryDecode(pkts, 1)
        assertNotNull(decoded)
        assertEquals('z'.code.toByte(), decoded[0])
    }

    @Test fun `k16 large-payload round-trip`() {
        val source  = ByteArray(1024) { i -> (i and 0xFF).toByte() }
        val codec   = RlncCodec(generationSize = 16)
        val encoded = codec.encode(source, 20)
        val pkts    = splitPackets(encoded, 20)
        val decoded = codec.tryDecode(pkts, 16)
        assertNotNull(decoded)
        assertEquals(source.toList(), decoded.take(source.size).toList())
    }

    @Test fun `empty received symbols returns null`() {
        val codec = RlncCodec(generationSize = 4)
        assertNull(codec.tryDecode(emptyList(), 4))
    }

    @Test fun `codec metadata`() {
        val codec = RlncCodec(generationSize = 4)
        assertEquals("RLNC-GF256", codec.codecName)
        assertEquals(0.05, codec.overheadFraction)
        assertEquals(0.toByte(), codec.deviceTierRequired)
        assertEquals(0, codec.fixedSymbolSizeBytes)
    }

    @Test fun `rejects generation_size = 0`() {
        try {
            RlncCodec(generationSize = 0)
            error("should have thrown")
        } catch (_: IllegalArgumentException) { /* expected */ }
    }

    @Test fun `rejects generation_size = 256`() {
        try {
            RlncCodec(generationSize = 256)
            error("should have thrown")
        } catch (_: IllegalArgumentException) { /* expected */ }
    }
}

// ── RlncDecoder low-level ─────────────────────────────────────────────────────

class RlncDecoderTest {

    @Test fun `starts at rank 0`() {
        val dec = RlncDecoder(generationSize = 4, symbolSize = 8)
        assertEquals(0, dec.rank)
        assertFalse(dec.isComplete)
    }

    @Test fun `linearly dependent packet does not increase rank`() {
        val dec   = RlncDecoder(generationSize = 3, symbolSize = 4)
        val coeff = byteArrayOf(1, 0, 0)
        val data  = byteArrayOf(10, 20, 30, 40)
        assertTrue(dec.addPacket(coeff, data))
        assertFalse(dec.addPacket(coeff, data), "duplicate must be rejected")
        assertEquals(1, dec.rank)
    }

    @Test fun `complete after K independent packets`() {
        val k = 3; val s = 2
        val dec = RlncDecoder(generationSize = k, symbolSize = s)
        for (i in 0 until k) {
            val coeff = ByteArray(k).also { it[i] = 1 }
            dec.addPacket(coeff, byteArrayOf((i + 1).toByte(), (i + 100).toByte()))
        }
        assertTrue(dec.isComplete)
    }

    @Test fun `tryDecode returns null when incomplete`() {
        assertNull(RlncDecoder(generationSize = 4, symbolSize = 4).tryDecode())
    }

    @Test fun `tryDecode preserves symbol ordering`() {
        val k = 3; val s = 2
        val dec = RlncDecoder(generationSize = k, symbolSize = s)
        val sources = listOf(byteArrayOf(0xAA.toByte(), 0xBB.toByte()),
                             byteArrayOf(0xCC.toByte(), 0xDD.toByte()),
                             byteArrayOf(0xEE.toByte(), 0xFF.toByte()))
        for (i in 0 until k) {
            val coeff = ByteArray(k).also { it[i] = 1 }
            dec.addPacket(coeff, sources[i])
        }
        val result = dec.tryDecode()
        assertNotNull(result)
        for (i in 0 until k) {
            assertEquals(sources[i][0], result[i * s],     "symbol[$i][0]")
            assertEquals(sources[i][1], result[i * s + 1], "symbol[$i][1]")
        }
    }
}

// ── RlncEncoder systematic mode ───────────────────────────────────────────────

class RlncEncoderTest {

    @Test fun `first K packets are systematic`() {
        val k = 4; val s = 3
        val syms = List(k) { i -> ByteArray(s) { j -> (i * s + j + 1).toByte() } }
        val enc  = RlncEncoder(syms, systematic = true)
        for (i in 0 until k) {
            val (coeff, data) = enc.nextPacket()
            // Coefficient vector must be e_i.
            for (j in 0 until k) {
                val want = if (j == i) 1.toByte() else 0.toByte()
                assertEquals(want, coeff[j], "pkt $i: coeff[$j]")
            }
            assertEquals(syms[i].toList(), data.toList(), "pkt $i data")
        }
    }

    @Test fun `repair packets have at least one non-zero coefficient`() {
        val syms = listOf(byteArrayOf(1,2,3), byteArrayOf(4,5,6), byteArrayOf(7,8,9))
        val enc  = RlncEncoder(syms, systematic = false)
        repeat(20) { i ->
            val (coeff, _) = enc.nextPacket()
            assertFalse(coeff.all { it == 0.toByte() },
                        "repair pkt $i has all-zero coefficient vector")
        }
    }
}
