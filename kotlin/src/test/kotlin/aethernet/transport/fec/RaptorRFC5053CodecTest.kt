// SPDX-License-Identifier: MIT
package aethernet.transport.fec

import org.junit.jupiter.api.Test
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.assertFailsWith

// ── Helpers ───────────────────────────────────────────────────────────────────

private fun splitSymbolsRaptor(encoded: ByteArray, count: Int): List<ByteArray> {
    val s = encoded.size / count
    return (0 until count).map { i -> encoded.copyOfRange(i * s, (i + 1) * s) }
}

class RaptorRFC5053CodecTest {

    private val codec = RaptorRFC5053Codec()

    // ── Metadata ──────────────────────────────────────────────────────────────

    @Test fun `codec name is Raptor-RFC5053`() {
        assertEquals("Raptor-RFC5053", codec.codecName)
    }

    @Test fun `device tier required is 0`() {
        assertEquals(0.toByte(), codec.deviceTierRequired)
    }

    @Test fun `fixed symbol size is 0 (variable)`() {
        assertEquals(0, codec.fixedSymbolSizeBytes)
    }

    @Test fun `overhead fraction is 0_05`() {
        assertTrue(abs(codec.overheadFraction - 0.05) < 1e-9, "overheadFraction should be 0.05")
    }

    // ── Encoder ───────────────────────────────────────────────────────────────

    @Test fun `encode K1 produces targetSymbolCount times symbolSize bytes`() {
        // sourceLen=512 → K=1, S=512. Output = 4 × 512 = 2048 bytes.
        val src     = ByteArray(512) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 4)
        assertEquals(4 * 512, encoded.size)
    }

    @Test fun `encode K2 produces correct total size`() {
        // sourceLen=1024 → K=2, S=512. Output = 5 × 512 = 2560 bytes.
        val src     = ByteArray(1024) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 5)
        assertEquals(5 * 512, encoded.size)
    }

    @Test fun `encode rejects targetSymbolCount smaller than K`() {
        // sourceLen=600 → K = ceil(600/512) = 2. targetSymbolCount=1 must throw.
        val src = ByteArray(600) { it.toByte() }
        assertFailsWith<IllegalArgumentException> {
            codec.encode(src, 1)
        }
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    @Test fun `K1 small payload round-trip`() {
        val src     = "hello raptor codec".toByteArray(Charsets.UTF_8)
        val encoded = codec.encode(src, 5)
        val pkts    = splitSymbolsRaptor(encoded, 5)
        // K=1: any single symbol reconstructs the source.
        val decoded = codec.tryDecode(pkts, 1)
        assertNotNull(decoded)
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    @Test fun `K1 full-symbol round-trip`() {
        val src     = ByteArray(512) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 6)
        val pkts    = splitSymbolsRaptor(encoded, 6)
        val decoded = codec.tryDecode(pkts, 1)
        assertNotNull(decoded)
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    @Test fun `K1 round-trip using last ESI only`() {
        // K=1 (sourceLen=512 ≤ S=512): all coded symbols are identical copies of
        // the single source block. Decode using only the last ESI to exercise
        // the rateless "any symbol suffices" property.
        val src     = ByteArray(512) { i -> ((i * 7 + 13) and 0xFF).toByte() }
        val encoded = codec.encode(src, 5)
        val pkts    = splitSymbolsRaptor(encoded, 5)
        // Present only ESI 4 (drop ESIs 0..3).
        val received = List(4) { ByteArray(0) } + listOf(pkts[4])
        val decoded  = codec.tryDecode(received, 1)
        assertNotNull(decoded, "single valid ESI must reconstruct K=1 source")
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    // ── Erasure recovery ──────────────────────────────────────────────────────

    @Test fun `decode with ESI 0 erased still recovers (K1)`() {
        // K=1: drop ESI 0, pass ESI 1..3.
        val src     = ByteArray(512) { i -> (i * 3 and 0xFF).toByte() }
        val encoded = codec.encode(src, 4)
        val pkts    = splitSymbolsRaptor(encoded, 4)

        val received = listOf(ByteArray(0)) + pkts.drop(1)
        val decoded  = codec.tryDecode(received, 1)
        assertNotNull(decoded, "ESI 1..3 must reconstruct K=1 source after ESI 0 erased")
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    @Test fun `K1 decode tolerates two erased symbols of six`() {
        // K=1: every coded symbol is an identical copy of the single source block.
        // Dropping ESIs 0 and 1 still leaves 4 valid ESIs — any one suffices.
        val src     = ByteArray(512) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 6)
        val pkts    = splitSymbolsRaptor(encoded, 6)

        val received = listOf(ByteArray(0), ByteArray(0)) + pkts.drop(2)
        val decoded  = codec.tryDecode(received, 1)
        assertNotNull(decoded, "4 valid ESIs (≥K=1) must reconstruct 1-source-symbol block")
        assertEquals(src.toList(), decoded!!.take(src.size).toList())
    }

    @Test fun `too many erasures returns null`() {
        // K=1, both ESIs erased.
        val src     = ByteArray(512) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 2)
        val pkts    = splitSymbolsRaptor(encoded, 2)

        val received = listOf(ByteArray(0), ByteArray(0))
        val decoded  = codec.tryDecode(received, 1)
        assertNull(decoded, "all symbols erased must return null")
    }

    @Test fun `decoded result encompasses original source bytes`() {
        val src     = ByteArray(512) { i -> (i and 0xFF).toByte() }
        val encoded = codec.encode(src, 3)
        val pkts    = splitSymbolsRaptor(encoded, 3)
        val decoded = codec.tryDecode(pkts, 1)
        assertNotNull(decoded)
        assertTrue(decoded!!.size >= src.size, "decoded buffer must be at least source.size bytes")
    }
}
