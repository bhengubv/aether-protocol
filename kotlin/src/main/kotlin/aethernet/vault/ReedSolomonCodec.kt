// SPDX-License-Identifier: MIT
//
// Systematic Cauchy-Reed-Solomon (K, N) erasure codec for the AetherNet vault — the production
// erasure-coding promised by the vault contract ("a file is split into K+M shards; any K shards
// reconstruct it"). Kotlin port of AetherNet.Vault.ReedSolomonCodec, byte-identical to the C#
// reference, the Go port, and every other language implementation.
//
// FIELD: arithmetic is over GF(2⁸) with primitive polynomial x⁸+x⁴+x³+x²+1 (0x11D, the AES/Rijndael
// polynomial), α = 2 — the SAME field as the transport RLNC codec. Identical field ⇒ identical parity
// bytes, which is what makes a parity shard scattered by one node decodable by any other node on the
// mesh.
//
// SHARD LAYOUT: the K DATA shards are SYSTEMATIC — shard i (0…K-1) is exactly
//   plaintext[i*shardSize .. i*shardSize+shardSize], zero-padded if short, shardSize = ceil(size/K)
// The M parity shards are real Cauchy-Reed-Solomon (MDS), so ANY K of the N shards reconstruct the
// original.
//
// MDS GUARANTEE: the parity rows form a Cauchy matrix C[i][j] = 1 / (x_i ⊕ y_j) over GF(256) with two
// disjoint sets of distinct field elements (y_0…y_{K-1} = 0…K-1, x_0…x_{M-1} = K…K+M-1). Every square
// submatrix of a Cauchy matrix is invertible, so stacked on the systematic K×K identity it yields a
// true MDS code: any K of the N generator rows are linearly independent ⇒ any K surviving shards
// reconstruct the original; K-1 or fewer is unrecoverable.

package aethernet.vault

/**
 * Systematic Reed-Solomon (K data + M parity) erasure codec over GF(2⁸). The K data shards are the
 * plaintext partitioned into equal zero-padded slices (byte-identical to the vault data layout); the
 * M parity shards are Cauchy-Reed-Solomon over the canonical 0x11D field. Any K of the K+M shards
 * reconstruct the original.
 *
 * @param k number of data shards (must be ≥ 1).
 * @param m number of parity shards (must be ≥ 0). K + M must be ≤ 256.
 */
class ReedSolomonCodec(private val k: Int, private val m: Int) {
    private val n: Int = k + m

    /**
     * Parity generator rows: `parity[i]` (i = 0…M-1) is the K-byte Cauchy coefficient vector for
     * parity shard K+i. Together with the implicit K×K systematic identity for the data shards these
     * form the full N×K MDS generator matrix.
     */
    private val parity: Array<ByteArray>

    init {
        require(k >= 1) { "K must be >= 1." }
        require(m >= 0) { "M must be >= 0." }
        require(k + m <= 256) { "K + M must be <= 256." }
        parity = buildCauchyParityMatrix(k, m)
    }

    /** Total shard count (K + M). */
    val shardCount: Int get() = n

    /** Number of data shards (K). */
    val dataShardCount: Int get() = k

    /** Number of parity shards (M). */
    val parityShardCount: Int get() = m

    /**
     * Encode [dataShards] (K byte arrays of equal length `shardSize`) into the full set of N shards.
     * Shards 0…K-1 are the data shards unchanged (systematic); shards K…N-1 are the M Reed-Solomon
     * parity shards. The returned shards are fresh copies — callers keep ownership of the input.
     */
    fun encode(dataShards: Array<ByteArray>): Array<ByteArray> {
        require(dataShards.size == k) { "Expected $k data shards, got ${dataShards.size}." }

        val shardSize = dataShards[0].size
        for (j in 0 until k) {
            require(dataShards[j].size == shardSize) { "All data shards must be the same length." }
        }

        val shards = arrayOfNulls<ByteArray>(n)

        // Systematic: the first K shards ARE the data shards (defensive copy — callers keep ownership).
        for (j in 0 until k) {
            shards[j] = dataShards[j].copyOf()
        }

        // Parity: shard K+i = Σ_j parity[i][j] · dataShards[j] over GF(256).
        for (i in 0 until m) {
            val parityShard = ByteArray(shardSize)
            val coeffs = parity[i]
            for (j in 0 until k) {
                val c = coeffs[j]
                if (c.toInt() == 0) continue
                val src = dataShards[j]
                for (b in 0 until shardSize) {
                    parityShard[b] = (parityShard[b].toInt() xor gfMul(c, src[b]).toInt()).toByte()
                }
            }
            shards[k + i] = parityShard
        }

        @Suppress("UNCHECKED_CAST")
        return shards as Array<ByteArray>
    }

    /**
     * Reconstruct the K data shards from any K available shards. [available] maps a shard index
     * (0…N-1) to its bytes; exactly K distinct entries are required, all of equal length. Returns the
     * K data shards (indices 0…K-1, in order). Throws [IllegalStateException] if fewer than K shards
     * are supplied (K-1 or fewer is unrecoverable).
     */
    fun decodeDataShards(available: Map<Int, ByteArray>): Array<ByteArray> {
        // Take the K lowest-indexed available shards (deterministic; any K suffice for an MDS code).
        val indices = available.keys
            .filter { it in 0 until n && available[it] != null }
            .sorted()
            .take(k)

        if (indices.size < k) {
            throw IllegalStateException("Cannot decode: only ${indices.size}/$k shards available.")
        }

        val shardSize = available[indices[0]]!!.size
        for (idx in indices) {
            check(available[idx]!!.size == shardSize) { "All supplied shards must be the same length." }
        }

        // Fast path: if all K data shards (0…K-1) are present, no inversion is needed — the data is the
        // systematic prefix verbatim. This is the common, byte-identical-to-canonical recovery case.
        if (indices.all { it < k }) {
            val direct = arrayOfNulls<ByteArray>(k)
            for (idx in indices) {
                direct[idx] = available[idx]!!.copyOf()
            }
            @Suppress("UNCHECKED_CAST")
            return direct as Array<ByteArray>
        }

        // General path: build the K×K generator submatrix A for the picked shard indices, invert it,
        // and apply A⁻¹ to the picked symbol-vectors to recover the K source (data) symbols.
        val a = Array(k) { r -> generatorRow(indices[r]) }
        val rhs = Array(k) { r -> available[indices[r]]!!.copyOf() }

        val inv = invertMatrix(a)

        val data = arrayOfNulls<ByteArray>(k)
        for (r in 0 until k) {
            val symbol = ByteArray(shardSize)
            for (c in 0 until k) {
                val coeff = inv[r][c]
                if (coeff.toInt() == 0) continue
                val src = rhs[c]
                for (b in 0 until shardSize) {
                    symbol[b] = (symbol[b].toInt() xor gfMul(coeff, src[b]).toInt()).toByte()
                }
            }
            data[r] = symbol
        }

        @Suppress("UNCHECKED_CAST")
        return data as Array<ByteArray>
    }

    // ── generator matrix ──────────────────────────────────────────────────────

    /**
     * The K-byte generator row for shard [index] (identity for a data shard, Cauchy coefficients for
     * a parity shard). Returns a copy — the caller mutates rows during inversion.
     */
    private fun generatorRow(index: Int): ByteArray {
        if (index < k) {
            // Systematic data row = standard basis vector e_index.
            val row = ByteArray(k)
            row[index] = 1
            return row
        }
        // Parity row.
        return parity[index - k].copyOf()
    }

    // ── GF(256) matrix inversion (Gauss-Jordan) ────────────────────────────────

    /**
     * Invert a K×K GF(256) matrix via Gauss-Jordan elimination. The Cauchy/identity stack guarantees
     * the picked submatrix is non-singular.
     */
    private fun invertMatrix(matrix: Array<ByteArray>): Array<ByteArray> {
        val size = k
        // Augment [matrix | I].
        val aug = Array(size) { r ->
            val row = ByteArray(2 * size)
            matrix[r].copyInto(row, 0, 0, size)
            row[size + r] = 1
            row
        }

        for (col in 0 until size) {
            // Find a pivot row at or below `col` with a non-zero entry in this column.
            var pivot = -1
            for (r in col until size) {
                if (aug[r][col].toInt() != 0) { pivot = r; break }
            }
            check(pivot >= 0) { "Singular matrix — shard set is not decodable." }

            if (pivot != col) {
                val tmp = aug[col]; aug[col] = aug[pivot]; aug[pivot] = tmp
            }

            // Normalise the pivot row so the pivot element becomes 1.
            val inv = gfInv(aug[col][col])
            for (c in 0 until 2 * size) {
                aug[col][c] = gfMul(aug[col][c], inv)
            }

            // Eliminate this column from every other row.
            for (r in 0 until size) {
                if (r == col) continue
                val factor = aug[r][col]
                if (factor.toInt() == 0) continue
                for (c in 0 until 2 * size) {
                    aug[r][c] = (aug[r][c].toInt() xor gfMul(factor, aug[col][c]).toInt()).toByte()
                }
            }
        }

        // Right half is the inverse.
        return Array(size) { r -> aug[r].copyOfRange(size, 2 * size) }
    }

    private companion object {
        // ── GF(2⁸) arithmetic — primitive polynomial 0x11D, α = 2 ──────────────────
        // Byte-for-byte identical field to the transport RLNC codec. Tables generated once at class
        // load; identical tables are what guarantee identical parity bytes across every language.

        private val EXP = ByteArray(512) // EXP[i] = α^i; doubled to avoid modular wrap in Mul
        private val LOG = ByteArray(256) // LOG[v] = log_α(v) for v ∈ [1, 255]

        init {
            var x = 1
            for (i in 0 until 255) {
                EXP[i] = x.toByte()
                LOG[x] = i.toByte()
                x = x shl 1
                if (x and 0x100 != 0) x = x xor 0x11D // reduce mod p(x) = x⁸+x⁴+x³+x²+1
            }
            for (i in 255 until 512) EXP[i] = EXP[i - 255]
            LOG[1] = 0
        }

        private fun gfMul(a: Byte, b: Byte): Byte {
            val ai = a.toInt() and 0xFF
            val bi = b.toInt() and 0xFF
            if (ai == 0 || bi == 0) return 0
            return EXP[(LOG[ai].toInt() and 0xFF) + (LOG[bi].toInt() and 0xFF)]
        }

        private fun gfInv(a: Byte): Byte {
            val ai = a.toInt() and 0xFF
            require(ai != 0) { "GF256: inverse of zero." }
            return EXP[255 - (LOG[ai].toInt() and 0xFF)]
        }

        /**
         * Build the M×K Cauchy parity matrix over GF(256): `C[i][j] = 1 / (x_i ⊕ y_j)` with disjoint
         * distinct element sets `y_j = j` (0…K-1) and `x_i = K + i` (K…K+M-1). Cauchy ⇒ every square
         * submatrix invertible ⇒ MDS when stacked on the systematic identity.
         */
        private fun buildCauchyParityMatrix(k: Int, m: Int): Array<ByteArray> =
            Array(m) { i ->
                val xi = (k + i).toByte()
                ByteArray(k) { j ->
                    // x_i and y_j are drawn from disjoint ranges, so x_i ⊕ y_j is never 0 → always invertible.
                    gfInv((xi.toInt() xor j).toByte())
                }
            }
    }
}
