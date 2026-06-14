// SPDX-License-Identifier: MIT
//
// Systematic Cauchy-Reed-Solomon (K, N) erasure codec for AetherNet.Vault — the production
// erasure-coding promised by the IVaultService contract ("a file is split into K+M shards; any K
// shards reconstruct it"). It replaces the byte-partition SIMULATION the in-memory service shipped
// (XOR-of-data for shard K, zero-fill for the rest — not real RS, recoverable only when the K data
// shards themselves survive).
//
// FIELD: arithmetic is over GF(2⁸) with primitive polynomial x⁸+x⁴+x³+x²+1 (0x11D, the AES/Rijndael
// polynomial), α = 2 — the SAME field as AetherNet.Transport.Fec's GF256 (RlncCodec). Identical field
// ⇒ identical parity bytes, which is what makes a parity shard scattered by one node decodable by any
// other node on the mesh (and byte-identical to the CircleAether vault, whose ReedSolomonCodec uses
// the same 0x11D field and the same Cauchy scheme).
//
// SHARD LAYOUT: the K DATA shards are SYSTEMATIC — shard i (0…K-1) is exactly
//   plaintext[i*shardSize .. i*shardSize+shardSize], zero-padded if short, shardSize = ceil(size/K)
// which is byte-identical to the in-memory service's existing data-shard slicing. The M parity shards
// are real Cauchy-Reed-Solomon (MDS), so ANY K of the N shards reconstruct the original.
//
// MDS GUARANTEE: the parity rows form a Cauchy matrix C[i][j] = 1 / (x_i ⊕ y_j) over GF(256) with two
// disjoint sets of distinct field elements (y_0…y_{K-1} = 0…K-1, x_0…x_{M-1} = K…K+M-1). Every square
// submatrix of a Cauchy matrix is invertible, so stacked on the systematic K×K identity it yields a
// true MDS code: any K of the N generator rows are linearly independent ⇒ any K surviving shards
// reconstruct the original; K-1 or fewer is unrecoverable.

using System.Runtime.CompilerServices;

namespace AetherNet.Vault;

/// <summary>
/// Systematic Reed-Solomon (K data + M parity) erasure codec over GF(2⁸). The K data shards are the
/// plaintext partitioned into equal zero-padded slices (byte-identical to the Vault data layout); the
/// M parity shards are Cauchy-Reed-Solomon over the canonical 0x11D field. Any K of the K+M shards
/// reconstruct the original.
/// </summary>
internal sealed class ReedSolomonCodec
{
    private readonly int _k;
    private readonly int _m;
    private readonly int _n;

    /// <summary>
    /// Parity generator rows: <c>_parity[i]</c> (i = 0…M-1) is the K-byte Cauchy coefficient vector for
    /// parity shard K+i. Together with the implicit K×K systematic identity for the data shards these
    /// form the full N×K MDS generator matrix.
    /// </summary>
    private readonly byte[][] _parity;

    /// <param name="k">Number of data shards (must be ≥ 1).</param>
    /// <param name="m">Number of parity shards (must be ≥ 0). K + M must be ≤ 256.</param>
    public ReedSolomonCodec(int k, int m)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), "K must be >= 1.");
        if (m < 0) throw new ArgumentOutOfRangeException(nameof(m), "M must be >= 0.");
        if (k + m > 256) throw new ArgumentOutOfRangeException(nameof(m), "K + M must be <= 256.");

        _k = k;
        _m = m;
        _n = k + m;
        _parity = BuildCauchyParityMatrix(k, m);
    }

    /// <summary>Total shard count (K + M).</summary>
    public int ShardCount => _n;

    /// <summary>
    /// Encode <paramref name="dataShards"/> (K byte[] of equal length <c>shardSize</c>) into the full set
    /// of N shards. Shards 0…K-1 are the data shards unchanged (systematic); shards K…N-1 are the M
    /// Reed-Solomon parity shards.
    /// </summary>
    public byte[][] Encode(byte[][] dataShards)
    {
        ArgumentNullException.ThrowIfNull(dataShards);
        if (dataShards.Length != _k)
            throw new ArgumentException($"Expected {_k} data shards, got {dataShards.Length}.", nameof(dataShards));

        int shardSize = dataShards[0].Length;
        for (int j = 0; j < _k; j++)
        {
            if (dataShards[j] is null || dataShards[j].Length != shardSize)
                throw new ArgumentException("All data shards must be non-null and the same length.", nameof(dataShards));
        }

        var shards = new byte[_n][];

        // Systematic: the first K shards ARE the data shards (defensive copy — callers keep ownership).
        for (int j = 0; j < _k; j++)
            shards[j] = (byte[])dataShards[j].Clone();

        // Parity: shard K+i = Σ_j parity[i][j] · dataShards[j] over GF(256).
        for (int i = 0; i < _m; i++)
        {
            var parityShard = new byte[shardSize];
            byte[] coeffs = _parity[i];
            for (int j = 0; j < _k; j++)
            {
                byte c = coeffs[j];
                if (c == 0) continue;
                byte[] src = dataShards[j];
                for (int b = 0; b < shardSize; b++)
                    parityShard[b] ^= GfMul(c, src[b]);
            }
            shards[_k + i] = parityShard;
        }

        return shards;
    }

    /// <summary>
    /// Reconstruct the K data shards from any K available shards. <paramref name="available"/> maps a
    /// shard index (0…N-1) to its bytes; exactly K distinct entries are required, all of equal length.
    /// Returns the K data shards (indices 0…K-1, in order). Throws <see cref="InvalidOperationException"/>
    /// if fewer than K shards are supplied.
    /// </summary>
    public byte[][] DecodeDataShards(IReadOnlyDictionary<int, byte[]> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        // Take the K lowest-indexed available shards (deterministic; any K suffice for an MDS code).
        var picked = available
            .Where(kv => kv.Key >= 0 && kv.Key < _n && kv.Value is not null)
            .OrderBy(kv => kv.Key)
            .Take(_k)
            .ToArray();

        if (picked.Length < _k)
            throw new InvalidOperationException(
                $"Cannot decode: only {picked.Length}/{_k} shards available.");

        int shardSize = picked[0].Value.Length;
        for (int r = 0; r < _k; r++)
        {
            if (picked[r].Value.Length != shardSize)
                throw new InvalidOperationException("All supplied shards must be the same length.");
        }

        // Fast path: if all K data shards (0…K-1) are present, no inversion is needed — the data is the
        // systematic prefix verbatim. This is the common, byte-identical-to-canonical recovery case.
        if (picked.All(kv => kv.Key < _k))
        {
            var direct = new byte[_k][];
            foreach (var kv in picked)
                direct[kv.Key] = (byte[])kv.Value.Clone();
            return direct;
        }

        // General path: build the K×K generator submatrix A for the picked shard indices, invert it,
        // and apply A⁻¹ to the picked symbol-vectors to recover the K source (data) symbols.
        var a = new byte[_k][];
        var rhs = new byte[_k][];
        for (int r = 0; r < _k; r++)
        {
            int idx = picked[r].Key;
            a[r] = GeneratorRow(idx);
            rhs[r] = (byte[])picked[r].Value.Clone();
        }

        byte[][] inv = InvertMatrix(a);

        var data = new byte[_k][];
        for (int r = 0; r < _k; r++)
        {
            var symbol = new byte[shardSize];
            for (int c = 0; c < _k; c++)
            {
                byte coeff = inv[r][c];
                if (coeff == 0) continue;
                byte[] src = rhs[c];
                for (int b = 0; b < shardSize; b++)
                    symbol[b] ^= GfMul(coeff, src[b]);
            }
            data[r] = symbol;
        }

        return data;
    }

    // ── generator matrix ──────────────────────────────────────────────────────

    /// <summary>The K-byte generator row for shard <paramref name="index"/> (identity for a data shard,
    /// Cauchy coefficients for a parity shard).</summary>
    private byte[] GeneratorRow(int index)
    {
        if (index < _k)
        {
            // Systematic data row = standard basis vector e_index.
            var row = new byte[_k];
            row[index] = 1;
            return row;
        }
        // Parity row.
        return (byte[])_parity[index - _k].Clone();
    }

    /// <summary>
    /// Build the M×K Cauchy parity matrix over GF(256): <c>C[i][j] = 1 / (x_i ⊕ y_j)</c> with disjoint
    /// distinct element sets <c>y_j = j</c> (0…K-1) and <c>x_i = K + i</c> (K…K+M-1). Cauchy ⇒ every
    /// square submatrix invertible ⇒ MDS when stacked on the systematic identity.
    /// </summary>
    private static byte[][] BuildCauchyParityMatrix(int k, int m)
    {
        var matrix = new byte[m][];
        for (int i = 0; i < m; i++)
        {
            var row = new byte[k];
            byte xi = (byte)(k + i);
            for (int j = 0; j < k; j++)
            {
                byte yj = (byte)j;
                // x_i and y_j are drawn from disjoint ranges, so x_i ⊕ y_j is never 0 → always invertible.
                row[j] = GfInv((byte)(xi ^ yj));
            }
            matrix[i] = row;
        }
        return matrix;
    }

    // ── GF(256) matrix inversion (Gauss-Jordan) ────────────────────────────────

    /// <summary>Invert a K×K GF(256) matrix via Gauss-Jordan elimination. The Cauchy/identity stack
    /// guarantees the picked submatrix is non-singular.</summary>
    private byte[][] InvertMatrix(byte[][] m)
    {
        int n = _k;
        // Augment [m | I].
        var aug = new byte[n][];
        for (int r = 0; r < n; r++)
        {
            aug[r] = new byte[2 * n];
            Array.Copy(m[r], 0, aug[r], 0, n);
            aug[r][n + r] = 1;
        }

        for (int col = 0; col < n; col++)
        {
            // Find a pivot row at or below `col` with a non-zero entry in this column.
            int pivot = -1;
            for (int r = col; r < n; r++)
            {
                if (aug[r][col] != 0) { pivot = r; break; }
            }
            if (pivot < 0)
                throw new InvalidOperationException("Singular matrix — shard set is not decodable.");

            if (pivot != col)
                (aug[col], aug[pivot]) = (aug[pivot], aug[col]);

            // Normalise the pivot row so the pivot element becomes 1.
            byte inv = GfInv(aug[col][col]);
            for (int c = 0; c < 2 * n; c++)
                aug[col][c] = GfMul(aug[col][c], inv);

            // Eliminate this column from every other row.
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                byte factor = aug[r][col];
                if (factor == 0) continue;
                for (int c = 0; c < 2 * n; c++)
                    aug[r][c] ^= GfMul(factor, aug[col][c]);
            }
        }

        // Right half is the inverse.
        var result = new byte[n][];
        for (int r = 0; r < n; r++)
        {
            result[r] = new byte[n];
            Array.Copy(aug[r], n, result[r], 0, n);
        }
        return result;
    }

    // ── GF(2⁸) arithmetic — primitive polynomial 0x11D, α = 2 ──────────────────
    // Byte-for-byte identical field to AetherNet.Transport.Fec.GF256 (that class is `internal`, so the
    // table-generation logic and the resulting Exp/Log tables are mirrored here rather than referenced
    // across the assembly boundary — identical tables are what guarantee identical parity bytes).

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomonCodec()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D; // reduce mod p(x) = x⁸+x⁴+x³+x²+1
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        Log[1] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GfMul(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return Exp[Log[a] + Log[b]];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GfInv(byte a)
    {
        if (a == 0) throw new DivideByZeroException("GF256: inverse of zero.");
        return Exp[255 - Log[a]];
    }
}
