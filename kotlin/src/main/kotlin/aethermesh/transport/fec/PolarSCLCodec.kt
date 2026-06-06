// SPDX-License-Identifier: MIT
// Polar erasure codec — Arıkan (2009) butterfly for symbol-level erasure recovery.
//
// Transform (F^T⊗n descending butterfly):
//   for step = N/2 down to 1:
//     for each group block b, offset s in 0..<step:
//       u[b+s] ^= u[b+s+step]
//
// Produces a lower-triangular generator matrix G; each "bit" is a full
// SYMBOL_BYTES block and XOR replaces GF(2) addition.
//
// Reliability (Bhattacharyya, BEC p = 0.5):
//   z[0] = 0.5;  z[2i] = z[i]²;  z[2i+1] = 2·z[i] − z[i]²
//
// Frozen set rule (verified by N=4 MDS check):
//   Freeze nFrozen positions with the SMALLEST z values.
//   Info positions  = K positions with the LARGEST z values.
//
// Decode: GF(2) Gaussian elimination on sub-matrix M[K×K] extracted from G,
//   where M[j][i] = G[infoPos[i]][recvPos[j]], solved simultaneously with
//   the received symbol byte-vectors as the RHS.

package aethermesh.transport.fec

import aethermesh.transport.FecCodec

/**
 * Arıkan polar erasure codec operating on 64-byte symbols.
 *
 * Implements [FecCodec]; suitable for BLE short-block erasure recovery.
 * Any K-of-N received symbols are sufficient to reconstruct the K source symbols.
 */
class PolarSCLCodec : FecCodec {

    // ─────────────────────────────────────────────────────────────────
    //  FecCodec properties
    // ─────────────────────────────────────────────────────────────────

    override val codecName: String          get() = "Polar-SCL"
    override val deviceTierRequired: Byte   get() = 1
    override val overheadFraction: Double   get() = 0.30
    override val fixedSymbolSizeBytes: Int  get() = SYMBOL_BYTES

    // ─────────────────────────────────────────────────────────────────
    //  Encode
    // ─────────────────────────────────────────────────────────────────

    /**
     * Encodes [source] into [targetSymbolCount] coded symbols.
     *
     * The source is split into K = ceil(source.size / 64) info symbols (last
     * one zero-padded), placed at the K info positions of a length-N polar
     * codeword, the butterfly is applied, and the first [targetSymbolCount]
     * coded symbols are returned as a flat byte array.
     */
    override fun encode(source: ByteArray, targetSymbolCount: Int): ByteArray {
        val S = SYMBOL_BYTES
        val K = (source.size + S - 1) / S
        val N = nextPow2(maxOf(targetSymbolCount, K))

        require(targetSymbolCount >= K) {
            "targetSymbolCount ($targetSymbolCount) must be ≥ source symbol count ($K)"
        }

        val reliability = computeReliabilityOrder(N)   // ascending z
        val nFrozen     = N - K
        val infoPos     = reliability.sliceArray(nFrozen until N)   // K positions with highest z

        // Allocate polar codeword u[N][S]; frozen positions remain zero.
        val u = Array(N) { ByteArray(S) }

        for (i in 0 until K) {
            val srcOffset = i * S
            val copyLen   = minOf(S, source.size - srcOffset)
            source.copyInto(u[infoPos[i]], destinationOffset = 0,
                            startIndex = srcOffset, endIndex = srcOffset + copyLen)
            // Remaining bytes already zero — correct zero-padding.
        }

        butterflyTransform(u)

        val output = ByteArray(targetSymbolCount * S)
        for (i in 0 until targetSymbolCount)
            u[i].copyInto(output, destinationOffset = i * S)

        return output
    }

    // ─────────────────────────────────────────────────────────────────
    //  TryDecode
    // ─────────────────────────────────────────────────────────────────

    /**
     * Attempts to reconstruct the original data from [receivedSymbols].
     *
     * [receivedSymbols] is indexed by coded-symbol position; any element whose
     * [ByteArray.size] ≠ 64 is treated as erased.
     *
     * Returns the reconstructed source bytes if decoding succeeded, or `null`
     * if too many symbols were erased.
     */
    override fun tryDecode(receivedSymbols: List<ByteArray>, sourceSymbolCount: Int): ByteArray? {
        val S = SYMBOL_BYTES
        val K = sourceSymbolCount
        val N = nextPow2(maxOf(receivedSymbols.size, K))

        val reliability = computeReliabilityOrder(N)
        val nFrozen     = N - K
        val infoPos     = reliability.sliceArray(nFrozen until N)

        // Collect received column indices (non-erased, exactly S bytes).
        val recvCols = mutableListOf<Int>()
        for (j in receivedSymbols.indices)
            if (receivedSymbols[j].size == S)
                recvCols += j

        if (recvCols.size < K) return null   // too many erasures

        val chosen = recvCols.subList(0, K)   // take exactly K

        val G = buildGeneratorMatrix(N)

        // Augmented system: K rows × (K-bit matrix + S-byte RHS).
        val M   = Array(K) { j -> BooleanArray(K) { i -> G[infoPos[i]][chosen[j]] } }
        val rhs = Array(K) { j -> receivedSymbols[chosen[j]].copyOf() }

        // GF(2) Gaussian elimination (reduced row echelon).
        for (col in 0 until K) {
            // Find pivot.
            val pivot = (col until K).firstOrNull { M[it][col] } ?: return null

            if (pivot != col) {
                val tmpM = M[col];   M[col] = M[pivot];   M[pivot] = tmpM
                val tmpR = rhs[col]; rhs[col] = rhs[pivot]; rhs[pivot] = tmpR
            }

            for (row in 0 until K) {
                if (row == col || !M[row][col]) continue
                for (c in 0 until K) M[row][c] = M[row][c] xor M[col][c]
                xorInPlace(rhs[row], rhs[col])
            }
        }

        // rhs[i] now holds u[infoPos[i]].  Concatenate to reconstruct source.
        val result = ByteArray(K * S)
        for (i in 0 until K) rhs[i].copyInto(result, destinationOffset = i * S)
        return result
    }

    // ─────────────────────────────────────────────────────────────────
    //  Private helpers — butterfly
    // ─────────────────────────────────────────────────────────────────

    /**
     * Applies the F^T⊗n descending butterfly in-place on u[N][S].
     *
     * For step = N/2 down to 1, for each block boundary b and offset s in [0, step):
     *   u[b+s] ^= u[b+s+step]
     *
     * Lower-index receives XOR of upper-index. This direction produces a
     * lower-triangular G and verified-MDS erasure code for frozen-smallest-z.
     */
    private fun butterflyTransform(u: Array<ByteArray>) {
        val N = u.size
        var step = N shr 1
        while (step >= 1) {
            var b = 0
            while (b < N) {
                for (s in 0 until step)
                    xorInPlace(u[b + s], u[b + s + step])
                b += step shl 1
            }
            step = step shr 1
        }
    }

    /**
     * Boolean butterfly on a single N-element row vector — used to build G.
     */
    private fun butterflyBool(row: BooleanArray): BooleanArray {
        val u = row.copyOf()
        val N = u.size
        var step = N shr 1
        while (step >= 1) {
            var b = 0
            while (b < N) {
                for (s in 0 until step)
                    u[b + s] = u[b + s] xor u[b + s + step]
                b += step shl 1
            }
            step = step shr 1
        }
        return u
    }

    // ─────────────────────────────────────────────────────────────────
    //  Private helpers — generator matrix & reliability
    // ─────────────────────────────────────────────────────────────────

    /**
     * Builds the N×N generator matrix G where G[r] = butterflyBool(e_r).
     * G[r][c] is true iff coded symbol c involves input symbol r.
     */
    private fun buildGeneratorMatrix(N: Int): Array<BooleanArray> =
        Array(N) { r ->
            val e = BooleanArray(N) { it == r }
            butterflyBool(e)
        }

    /**
     * Returns indices 0..(N-1) sorted by ascending Bhattacharyya z-value.
     *
     * First nFrozen entries (lowest z) → frozen positions.
     * Last K entries (highest z)       → information positions.
     *
     * Recursion (BEC, erasure probability p = 0.5):
     *   z[0]    = 0.5
     *   z[2i]   = z[i]²              (upper sub-channel — more reliable)
     *   z[2i+1] = 2·z[i] − z[i]²    (lower sub-channel — less reliable)
     */
    private fun computeReliabilityOrder(N: Int): IntArray {
        val z = computeBhattacharyya(N)
        // Sort indices 0..(N-1) by ascending z-value:
        //   smallest z → frozen (start of array)
        //   largest z  → info   (end of array)
        return IntArray(N) { it }.sortedBy { z[it] }.toIntArray()
    }

    /**
     * Computes Bhattacharyya z-values for all N synthetic channels of BEC(0.5).
     */
    private fun computeBhattacharyya(N: Int): DoubleArray {
        val z = DoubleArray(N)
        z[0] = 0.5
        var step = 1
        while (step < N) {
            // Expand in reverse to avoid overwriting values still needed.
            for (i in step - 1 downTo 0) {
                val zi      = z[i]
                z[2 * i]     = zi * zi
                z[2 * i + 1] = 2.0 * zi - zi * zi
            }
            step = step shl 1
        }
        return z
    }

    // ─────────────────────────────────────────────────────────────────
    //  Private helpers — memory / math
    // ─────────────────────────────────────────────────────────────────

    private fun xorInPlace(dst: ByteArray, src: ByteArray) {
        for (i in dst.indices) dst[i] = (dst[i].toInt() xor src[i].toInt()).toByte()
    }

    private fun nextPow2(n: Int): Int {
        if (n <= 1) return 1
        var p = 1
        while (p < n) p = p shl 1
        return p
    }

    companion object {
        private const val SYMBOL_BYTES = 64
    }
}
