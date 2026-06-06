// SPDX-License-Identifier: MIT
// Raptor fountain codec — RFC 5053 §5 LT encoding over GF(2) symbol blocks.
//
// Degree distribution (RFC 5053 Appendix B Table 2):
//   degrees  = [1,   2,    3,    4,    10,    11,    40  ]
//   cumF (×2^20) = [10241, 491582, 712794, 831695, 948446, 1032189, 1048576]
//
// Coded symbol generation for ESI x over K source symbols of size S bytes:
//   1. seed = x * 0x9E3779B9 XOR 0xDEADBEEF
//      v20  = xorshift32(seed) AND 0xFFFFF       (20-bit for degree table)
//      a    = 1 + xorshift32(v20) % (K-1)        (step 1..K-1)
//      b    = xorshift32(a)       % K             (start 0..K-1)
//      d    = Degree(v20, K)                      (from RFC 5053 table)
//   2. Connected source indices: { (b + a*i) mod K  for i = 0..<d }
//   3. Coded symbol = XOR of d source byte-blocks.
//
// Decoding: Belief-Propagation (peeling) + GF(2) Gaussian Elimination residual.

package aethernet.transport.fec

import aethernet.transport.FecCodec

/**
 * Rateless Raptor fountain codec (RFC 5053 LT layer) operating on byte-block symbols.
 *
 * Implements [FecCodec] with variable symbol size ([fixedSymbolSizeBytes] = 0).
 * Any K + small overhead of received encoded symbols can reconstruct the K source symbols.
 * Belief-Propagation handles the common case; Gaussian Elimination covers residual failures.
 */
class RaptorRFC5053Codec : FecCodec {

    // ─────────────────────────────────────────────────────────────────
    //  RFC 5053 Appendix B — LT degree distribution
    // ─────────────────────────────────────────────────────────────────

    private val degrees = intArrayOf(1, 2, 3, 4, 10, 11, 40)
    private val cumF    = longArrayOf(10241L, 491582L, 712794L, 831695L, 948446L, 1032189L, 1048576L)

    // ─────────────────────────────────────────────────────────────────
    //  FecCodec properties
    // ─────────────────────────────────────────────────────────────────

    override val codecName: String         get() = "Raptor-RFC5053"
    override val deviceTierRequired: Byte  get() = 0
    override val overheadFraction: Double  get() = 0.05
    override val fixedSymbolSizeBytes: Int get() = 0   // variable — determined per-transfer

    // ─────────────────────────────────────────────────────────────────
    //  Encode
    // ─────────────────────────────────────────────────────────────────

    /**
     * Splits [source] into K source symbols (last zero-padded to symbolSize),
     * then produces [targetSymbolCount] LT-encoded symbols with ESI 0, 1, …
     */
    override fun encode(source: ByteArray, targetSymbolCount: Int): ByteArray {
        val (K, S) = sourceParams(source.size)

        require(targetSymbolCount >= K) {
            "targetSymbolCount ($targetSymbolCount) must be ≥ source symbol count ($K)"
        }

        // Build padded source symbols.
        val src = Array(K) { i ->
            val off = i * S
            val len = minOf(S, source.size - off)
            ByteArray(S).also { sym ->
                source.copyInto(sym, 0, off, off + len)
            }
        }

        val output = ByteArray(targetSymbolCount * S)
        for (esi in 0 until targetSymbolCount) {
            val sym = ByteArray(S)
            for (idx in getConnectedIndices(esi, K))
                xorInPlace(sym, src[idx])
            sym.copyInto(output, esi * S)
        }
        return output
    }

    // ─────────────────────────────────────────────────────────────────
    //  TryDecode
    // ─────────────────────────────────────────────────────────────────

    /**
     * Attempts to reconstruct source from [receivedSymbols].
     *
     * The index of each element is its ESI. An erased symbol has [ByteArray.size] ≠ S.
     * Returns reconstructed source bytes on success, or `null` if too many were lost.
     */
    override fun tryDecode(receivedSymbols: List<ByteArray>, sourceSymbolCount: Int): ByteArray? {
        val K = sourceSymbolCount
        if (K <= 0) return null

        // Infer symbol size from first non-erased symbol.
        val S = receivedSymbols.firstOrNull { it.isNotEmpty() }?.size ?: return null

        // Collect non-erased (ESI, data) pairs.
        val received = receivedSymbols.mapIndexedNotNull { j, sym ->
            if (sym.size == S) Pair(j, sym.copyOf()) else null
        }
        if (received.size < K) return null

        val recovered  = arrayOfNulls<ByteArray>(K)
        var recoveredCount = 0

        // adj[j] = mutable list of source indices still unknown for coded symbol j.
        val adj = Array(received.size) { j ->
            getConnectedIndices(received[j].first, K).toMutableList()
        }
        val rhs = Array(received.size) { j -> received[j].second.copyOf() }

        // ── Belief Propagation (peeling) ─────────────────────────────────────
        val degree1 = ArrayDeque<Int>()
        for (j in adj.indices) if (adj[j].size == 1) degree1.addLast(j)

        while (degree1.isNotEmpty() && recoveredCount < K) {
            val j = degree1.removeFirst()
            if (adj[j].size != 1) continue

            val si = adj[j][0]
            if (recovered[si] != null) continue

            recovered[si] = rhs[j].copyOf()
            recoveredCount++

            for (jj in adj.indices) {
                val pos = adj[jj].indexOf(si)
                if (pos < 0) continue
                adj[jj].removeAt(pos)
                xorInPlace(rhs[jj], recovered[si]!!)
                if (adj[jj].size == 1) degree1.addLast(jj)
            }
        }

        if (recoveredCount == K)
            return buildDecoded(recovered, K, S)

        // ── Gaussian Elimination residual ─────────────────────────────────────
        val unknownSrc = (0 until K).filter { recovered[it] == null }
        val U          = unknownSrc.size

        // Map unknown source index → GE column.
        val colOf = unknownSrc.mapIndexed { c, si -> si to c }.toMap()

        // Pick U coded symbols that each have ≥ 1 unknown dependency.
        val useRows = adj.indices.filter { j -> adj[j].isNotEmpty() }
        if (useRows.size < U) return null

        val M  = Array(U) { r ->
            val j  = useRows[r]
            BooleanArray(U) { c -> adj[j].contains(unknownSrc[c]) }
        }
        val ge = Array(U) { r -> rhs[useRows[r]].copyOf() }

        // GF(2) RREF.
        for (col in 0 until U) {
            val pivot = (col until U).firstOrNull { M[it][col] } ?: return null
            if (pivot != col) {
                val tmpM = M[col]; M[col] = M[pivot]; M[pivot] = tmpM
                val tmpG = ge[col]; ge[col] = ge[pivot]; ge[pivot] = tmpG
            }
            for (row in 0 until U) {
                if (row == col || !M[row][col]) continue
                for (c in 0 until U) M[row][c] = M[row][c] xor M[col][c]
                xorInPlace(ge[row], ge[col])
            }
        }

        for (r in 0 until U) {
            recovered[unknownSrc[r]] = ge[r]
            recoveredCount++
        }

        if (recoveredCount < K) return null
        return buildDecoded(recovered, K, S)
    }

    // ─────────────────────────────────────────────────────────────────
    //  LT symbol generation
    // ─────────────────────────────────────────────────────────────────

    /**
     * Returns the K source-symbol indices XOR'd together to produce coded symbol [esi].
     *
     * Seed derivation mirrors the C# implementation:
     *   seed = esi * 0x9E3779B9 XOR 0xDEADBEEF
     *   v20  = xorshift32(seed) AND 0xFFFFF
     *   a    = 1 + xorshift32(v20) % (K-1)
     *   b    = xorshift32(a)       % K
     *   connected = { (b + a*i) mod K | i = 0..<d }
     */
    private fun getConnectedIndices(esi: Int, K: Int): IntArray {
        var s    = (esi.toLong() * 0x9E3779B9L xor 0xDEADBEEFL).toInt().toUInt()
        s        = xorshift32(s)
        val v20  = (s and 0xFFFFFu).toLong()

        val d = degreeFromTable(v20, K)

        s = xorshift32(v20.toUInt())
        val a = if (K > 1) 1 + (s % (K - 1).toUInt()).toInt() else 1

        s = xorshift32(s)
        val b = (s % K.toUInt()).toInt()

        return IntArray(d) { i -> (b + a * i) % K }
    }

    /** Maps 20-bit value [v] to a degree using RFC 5053 Appendix B table, capped at K. */
    private fun degreeFromTable(v: Long, K: Int): Int {
        for (i in cumF.indices)
            if (v < cumF[i])
                return minOf(K, degrees[i])
        return minOf(K, degrees.last())
    }

    /** Xorshift32 PRNG (Marsaglia 2003). Maps 0 → 1 to avoid degenerate state. */
    private fun xorshift32(x: UInt): UInt {
        var v = if (x == 0u) 1u else x
        v = v xor (v shl 13)
        v = v xor (v shr 17)
        v = v xor (v shl 5)
        return v
    }

    // ─────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────

    private fun sourceParams(sourceLen: Int): Pair<Int, Int> {
        if (sourceLen == 0) return Pair(1, TARGET_SYMBOL_BYTES)
        val K = maxOf(1, (sourceLen + TARGET_SYMBOL_BYTES - 1) / TARGET_SYMBOL_BYTES)
        val S = (sourceLen + K - 1) / K
        return Pair(K, S)
    }

    private fun buildDecoded(recovered: Array<ByteArray?>, K: Int, S: Int): ByteArray {
        val result = ByteArray(K * S)
        for (i in 0 until K) recovered[i]!!.copyInto(result, i * S)
        return result
    }

    private fun xorInPlace(dst: ByteArray, src: ByteArray) {
        for (i in dst.indices) dst[i] = (dst[i].toInt() xor src[i].toInt()).toByte()
    }

    companion object {
        private const val TARGET_SYMBOL_BYTES = 512
    }
}
