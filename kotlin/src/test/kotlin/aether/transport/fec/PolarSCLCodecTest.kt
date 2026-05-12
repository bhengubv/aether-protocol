// SPDX-License-Identifier: MIT
package aether.transport.fec

import org.junit.jupiter.api.Test
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.assertFailsWith

// ── Helpers ───────────────────────────────────────────────────────────────────

private fun splitSymbolsPolar(encoded: ByteArray, count: Int): List<ByteArray> {
    val s = encoded.size / count
    return (0 until count).map { i -> encoded.copyOfRange(i * s, (i + 1) * s) }
}

class PolarSCLCodecTest {

    private val codec = PolarSCLCodec()

    // ── Metadata ──────────────────────────────────────────────────────────────

    @Test fun `codec name is Polar-SCL`() {
        assertEquals("Polar-SCL", codec.codecName)
    }

    @Test fun `device tier required is 1`() {
        assertEquals(1.toByte(), codec.deviceTierRequired)
    }

    @Test fun `fixed symbol size is 64 bytes`() {
        assertEquals(64, codec.fixedSymbolSizeBytes)
    }

    @Test fun `overhead fraction is 0_30`() {
        assertTrue(abs(codec.overheadFraction - 0.30) < 1e-9, "overheadFraction should be 0.30")
    }

    // ── Encoder ───────────────────────────────────────────────────────────────

    @Test fun `encode produces targetSymbolCount times 64 bytes`() {
        val src     = ByteArray(64) { it.toByte() }
        val encoded = codec.encode(src, 4)
        assertEquals(4 * 64, encoded.size)
    }

    @Test fun `encode with partial symbol pads and returns correct size`() {
        // Source is 100 bytes → K = ceil(100/64) = 2 source symbols, pad to 2×64.
        val src     = ByteArray(100) { it.toByte() }
        val encoded = codec.encode(src, 4)
        assertEquals(4 * 64, encoded.size)
    }

    @Test fun `encode rejects targetSymbolCount smaller than source symbol count`() {
        // 192 bytes = exactly 3 symbols; targetSymbolCount=2 must throw.
        val src = ByteArray(192) { it.toByte() }
        assertFailsWith<IllegalArgumentException> {
            codec.encode(src, 2)
        }
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    @Test fun `K1 single-symbol round-trip`() {
        val src     = ByteArray(64) { (it xor 0x5A).toByte() }
        val encoded = codec.encode(src, 2)
        val pkts    = splitSymbolsPolar(encoded, 2)
        val decoded = codec.tryDecode(pkts, 1)
        assertNotNull(decoded)
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    @Test fun `K2 round-trip`() {
        val src     = ByteArray(128) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 4)
        val pkts    = splitSymbolsPolar(encoded, 4)
        val decoded = codec.tryDecode(pkts, 2)
        assertNotNull(decoded)
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    @Test fun `K4 round-trip exact symbols (N=K, no overhead)`() {
        // targetSymbolCount=K=4 → N=nextPow2(4)=4; all 4 positions are info,
        // the 4×4 butterfly matrix is always invertible → guaranteed round-trip.
        val src     = ByteArray(256) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 4)
        val pkts    = splitSymbolsPolar(encoded, 4)
        val decoded = codec.tryDecode(pkts, 4)
        assertNotNull(decoded)
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    // ── Erasure recovery ──────────────────────────────────────────────────────

    @Test fun `decode with first two symbols erased still recovers`() {
        // K=2, N=4. Drop coded symbols 0 and 1; keep 2 and 3.
        val src     = ByteArray(128) { i -> (i * 7 and 0xFF).toByte() }
        val encoded = codec.encode(src, 4)
        val pkts    = splitSymbolsPolar(encoded, 4)

        // Signal erasure by passing an empty ByteArray at that position.
        val received = listOf(ByteArray(0), ByteArray(0), pkts[2], pkts[3])
        val decoded  = codec.tryDecode(received, 2)
        assertNotNull(decoded, "any 2-of-4 coded symbols must reconstruct K=2 source")
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    @Test fun `decode with last two symbols erased still recovers`() {
        // K=2, N=4. Drop coded symbols 2 and 3; keep 0 and 1.
        val src     = ByteArray(128) { i -> (i * 3 and 0xFF).toByte() }
        val encoded = codec.encode(src, 4)
        val pkts    = splitSymbolsPolar(encoded, 4)

        val received = listOf(pkts[0], pkts[1], ByteArray(0), ByteArray(0))
        val decoded  = codec.tryDecode(received, 2)
        assertNotNull(decoded, "coded symbols 0 and 1 must reconstruct K=2 source")
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    @Test fun `too many erasures returns null`() {
        // K=2; give only 1 valid symbol → can't decode.
        val src     = ByteArray(128) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 4)
        val pkts    = splitSymbolsPolar(encoded, 4)

        val received = listOf(pkts[0], ByteArray(0), ByteArray(0), ByteArray(0))
        val decoded  = codec.tryDecode(received, 2)
        assertNull(decoded, "fewer than K valid symbols must return null")
    }

    @Test fun `decode result size is K times symbol bytes`() {
        val src     = ByteArray(64) { it.toByte() }
        val encoded = codec.encode(src, 2)
        val pkts    = splitSymbolsPolar(encoded, 2)
        val decoded = codec.tryDecode(pkts, 1)
        assertNotNull(decoded)
        assertTrue(decoded!!.size >= src.size, "decoded must be at least as large as source")
    }
}
