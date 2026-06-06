// SPDX-License-Identifier: MIT
// RLNC Engine — Random Linear Network Coding over GF(2⁸).
//
// Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1 (0x11D — same as AES Rijndael).
//
// Components
// ──────────
//   GF256          — GF(2⁸) log/exp tables and arithmetic helpers.
//   RlncEncoder    — systematic + random-repair packet generation.
//   RlncDecoder    — incremental Gauss-Jordan elimination.
//   RlncCodec      — FecCodec adapter (implements FecCodec interface).
//
// Wire format per packet:
//   [ K coefficient bytes ][ symbolSize data bytes ]

package aethermesh.transport.rlnc

import aethermesh.transport.FecCodec
import java.security.SecureRandom

// ── GF(2⁸) arithmetic ─────────────────────────────────────────────────────────

/** Singleton that owns the precomputed GF(2⁸) log/exp tables. */
private object Gf256 {
    val exp: ByteArray
    val log: ByteArray

    init {
        val e = ByteArray(512)
        val l = ByteArray(256)
        var x = 1
        for (i in 0 until 255) {
            e[i] = x.toByte()
            l[x] = i.toByte()
            x = x shl 1
            if (x and 0x100 != 0) x = x xor 0x11D // reduce mod p(x)
            x = x and 0xFF
        }
        for (i in 255 until 512) e[i] = e[i - 255]
        l[1] = 0 // log_α(1) = 0
        exp = e
        log = l
    }
}

private fun gf256Mul(a: Int, b: Int): Int {
    if (a == 0 || b == 0) return 0
    return Gf256.exp[(Gf256.log[a].toInt() and 0xFF) + (Gf256.log[b].toInt() and 0xFF)].toInt() and 0xFF
}

private fun gf256Inv(a: Int): Int {
    require(a != 0) { "rlnc: GF256 inverse of zero" }
    return Gf256.exp[255 - (Gf256.log[a].toInt() and 0xFF)].toInt() and 0xFF
}

private fun gf256Add(a: Int, b: Int): Int = a xor b

// ── RlncEncoder ───────────────────────────────────────────────────────────────

/**
 * Encodes K source symbols as systematic + random-repair RLNC packets.
 *
 * The first [generationSize] packets are systematic (identity coefficient vectors;
 * byte-identical to the source symbols).  Subsequent packets use random GF(2⁸)
 * coefficients.
 */
class RlncEncoder(
    private val source:      List<ByteArray>,
    private val systematic:  Boolean = true,
    private val random:      SecureRandom = SecureRandom(),
) {
    private var nextIndex = 0

    val generationSize: Int get() = source.size
    val symbolSize:     Int get() = source[0].size

    /**
     * Returns a `Pair(coefficients, encodedSymbol)` for the next packet.
     * First [generationSize] packets are systematic when `systematic = true`.
     */
    fun nextPacket(): Pair<ByteArray, ByteArray> {
        val k = generationSize
        val coefficients: ByteArray
        val encodedSymbol: ByteArray

        if (systematic && nextIndex < k) {
            // Systematic: e_i coefficient vector.
            coefficients             = ByteArray(k)
            coefficients[nextIndex]  = 1
            encodedSymbol            = source[nextIndex].copyOf()
        } else {
            // Repair: random GF(2⁸) coefficient vector.
            coefficients = ByteArray(k).also { random.nextBytes(it) }
            if (coefficients.all { it == 0.toByte() }) coefficients[0] = 1
            encodedSymbol = encodeSymbol(coefficients)
        }

        nextIndex++
        return Pair(coefficients, encodedSymbol)
    }

    private fun encodeSymbol(coefficients: ByteArray): ByteArray {
        val s   = symbolSize
        val out = ByteArray(s)
        for (kIdx in source.indices) {
            val c = coefficients[kIdx].toInt() and 0xFF
            if (c == 0) continue
            val sym = source[kIdx]
            for (i in 0 until s) {
                out[i] = gf256Add(out[i].toInt() and 0xFF, gf256Mul(c, sym[i].toInt() and 0xFF)).toByte()
            }
        }
        return out
    }
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

/**
 * Incremental Gauss-Jordan decoder over GF(2⁸).
 *
 * Maintains the accumulated coefficient matrix in Reduced Row Echelon Form (RREF)
 * as packets arrive.  Decoding is immediate when [rank] equals [generationSize].
 *
 * @param generationSize  K — number of source symbols per generation.
 * @param symbolSize      Byte length of each symbol.
 */
class RlncDecoder(
    val generationSize: Int,
    val symbolSize:     Int,
) {
    private val pivotCoeff = arrayOfNulls<ByteArray>(generationSize)
    private val pivotData  = arrayOfNulls<ByteArray>(generationSize)
    private var _rank      = 0

    /** Number of linearly independent packets received. */
    val rank:       Int     get() = _rank
    /** ``true`` when all K source symbols can be reconstructed. */
    val isComplete: Boolean get() = _rank == generationSize

    /**
     * Submit an encoded packet. Returns ``true`` if rank increased.
     *
     * @param coefficients  K-byte GF(2⁸) coefficient vector.
     * @param encodedSymbol [symbolSize]-byte encoded data.
     */
    fun addPacket(coefficients: ByteArray, encodedSymbol: ByteArray): Boolean {
        val k   = generationSize
        val s   = symbolSize
        val row  = coefficients.copyOf()
        val data = encodedSymbol.copyOf()

        // ── Forward-elimination ──────────────────────────────────────────────
        for (j in 0 until k) {
            val ri = row[j].toInt() and 0xFF
            if (ri == 0 || pivotCoeff[j] == null) continue
            val c  = ri
            val pr = pivotCoeff[j]!!
            val pd = pivotData[j]!!
            for (i in 0 until k) row[i]  = gf256Add(row[i].toInt() and 0xFF,  gf256Mul(c, pr[i].toInt() and 0xFF)).toByte()
            for (i in 0 until s) data[i] = gf256Add(data[i].toInt() and 0xFF, gf256Mul(c, pd[i].toInt() and 0xFF)).toByte()
        }

        // ── Find pivot column ────────────────────────────────────────────────
        val pivotCol = (0 until k).firstOrNull { row[it] != 0.toByte() } ?: return false

        // ── Normalise ────────────────────────────────────────────────────────
        val inv = gf256Inv(row[pivotCol].toInt() and 0xFF)
        for (i in 0 until k) row[i]  = gf256Mul(inv, row[i].toInt() and 0xFF).toByte()
        for (i in 0 until s) data[i] = gf256Mul(inv, data[i].toInt() and 0xFF).toByte()

        // ── Back-substitution ────────────────────────────────────────────────
        for (r in 0 until k) {
            val pr = pivotCoeff[r] ?: continue
            val c  = pr[pivotCol].toInt() and 0xFF
            if (c == 0) continue
            val pd = pivotData[r]!!
            for (i in 0 until k) pr[i] = gf256Add(pr[i].toInt() and 0xFF, gf256Mul(c, row[i].toInt() and 0xFF)).toByte()
            for (i in 0 until s) pd[i] = gf256Add(pd[i].toInt() and 0xFF, gf256Mul(c, data[i].toInt() and 0xFF)).toByte()
        }

        pivotCoeff[pivotCol] = row
        pivotData[pivotCol]  = data
        _rank++
        return true
    }

    /**
     * Returns decoded source bytes (concatenated) when [isComplete], or ``null``.
     */
    fun tryDecode(): ByteArray? {
        if (!isComplete) return null
        val result = ByteArray(generationSize * symbolSize)
        for (j in 0 until generationSize) {
            pivotData[j]!!.copyInto(result, j * symbolSize)
        }
        return result
    }
}

// ── RlncCodec : FecCodec ──────────────────────────────────────────────────────

/**
 * [FecCodec] adapter for RLNC over GF(2⁸).
 *
 * Each encoded packet is ``[ K coefficient bytes ][ symbolSize data bytes ]``.
 *
 * @param generationSize  K — source symbols per generation. Range: [1, 255].
 */
class RlncCodec(generationSize: Int = 16) : FecCodec {

    private val k: Int = generationSize.also {
        require(it in 1..255) { "rlnc: generationSize must be in [1, 255]" }
    }

    override val codecName:            String = "RLNC-GF256"
    override val deviceTierRequired:   Byte   = 0
    override val overheadFraction:     Double = 0.05
    override val fixedSymbolSizeBytes: Int    = 0

    override fun encode(source: ByteArray, targetSymbolCount: Int): ByteArray {
        require(source.isNotEmpty()) { "rlnc: source must not be empty" }
        val symbolSize  = (source.size + k - 1) / k
        val packetSize  = k + symbolSize
        val symbols     = splitIntoSymbols(source, k, symbolSize)
        val enc         = RlncEncoder(symbols, systematic = true)
        val output      = ByteArray(targetSymbolCount * packetSize)

        for (i in 0 until targetSymbolCount) {
            val (coeff, data) = enc.nextPacket()
            val offset        = i * packetSize
            coeff.copyInto(output, offset)
            data.copyInto(output, offset + k)
        }
        return output
    }

    override fun tryDecode(receivedSymbols: List<ByteArray>, sourceSymbolCount: Int): ByteArray? {
        if (receivedSymbols.isEmpty()) return null
        val symbolSize = receivedSymbols[0].size - k
        if (symbolSize <= 0) return null

        val dec = RlncDecoder(k, symbolSize)
        for (pkt in receivedSymbols) {
            dec.addPacket(pkt.copyOfRange(0, k), pkt.copyOfRange(k, pkt.size))
            if (dec.isComplete) break
        }
        return dec.tryDecode()
    }
}

private fun splitIntoSymbols(source: ByteArray, k: Int, symbolSize: Int): List<ByteArray> =
    List(k) { i ->
        val sym    = ByteArray(symbolSize)
        val offset = i * symbolSize
        val length = minOf(symbolSize, source.size - offset)
        if (length > 0) source.copyInto(sym, 0, offset, offset + length)
        sym
    }
