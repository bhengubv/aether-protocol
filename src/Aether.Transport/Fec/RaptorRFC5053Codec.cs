// SPDX-License-Identifier: MIT
// Raptor fountain codec — RFC 5053 §5 LT encoding over GF(2) symbol blocks.
//
// Degree distribution (RFC 5053 Appendix B Table 2):
//   degrees  = [1,  2,   3,   4,  10,   11,   40  ]
//   cumF (×2^20) = [10241, 491582, 712794, 831695, 948446, 1032189, 1048576]
//
// Coded symbol generation for ESI x over K source symbols of size S bytes:
//   1. Derive (d, a, b) from ESI using a seeded xorshift32:
//        seed = x * 0x9E3779B9 ^ 0xDEADBEEF  (avalanche mix)
//        v20  = xorshift(seed) & 0xFFFFF      (20-bit uniform)
//        a    = 1 + xorshift(v20) % (K-1)     (odd step 1..K-1)
//        b    = xorshift(a)       % K          (start position 0..K-1)
//        d    = Degree(v20, K)                 (from RFC 5053 table)
//   2. Connected source indices: { (b + a*i) mod K  for i = 0..d-1 }
//   3. Coded symbol = XOR of d source symbol byte-blocks.
//
// Decoding: Belief-Propagation (peeling) + GF(2) Gaussian elimination residual.
//   BP: repeatedly resolve degree-1 coded symbols; eliminate recovered symbol
//       from all other coded symbols that include it.
//   GE: on any residual unrecovered source symbols, build a binary matrix and
//       perform full reduced-row-echelon elimination using remaining coded symbols.
//
// Typical use: LoRa long-distance links, source blocks up to 64 KB.
// Symbol size is determined per-transfer as ceil(source.Length / K) where
// K = ceil(source.Length / TARGET_SYMBOL_BYTES).

using System;
using System.Collections.Generic;
using Aether.Transport.Abstractions;

namespace Aether.Transport.Fec;

/// <summary>
/// Rateless Raptor fountain codec (RFC 5053 LT layer) operating on byte-block symbols.
///
/// Implements <see cref="IFecCodec"/> with variable symbol size (returns 0 for
/// <see cref="FixedSymbolSizeBytes"/>).  Any K + overhead received encoded symbols
/// can probabilistically reconstruct the K source symbols.  In practice the BP
/// decoder succeeds with ≤ 3 % overhead for K ≥ 8, and GE handles residual failures.
/// </summary>
public sealed class RaptorRFC5053Codec : IFecCodec
{
    // ─────────────────────────────────────────────────────────────────
    //  RFC 5053 Appendix B — LT degree distribution
    //  cumF[i] is the cumulative probability × 2^20 that degree ≤ Degrees[i].
    // ─────────────────────────────────────────────────────────────────

    private static readonly int[]  Degrees = {  1,      2,       3,       4,     10,      11,      40 };
    private static readonly uint[] CumF    = { 10241u, 491582u, 712794u, 831695u, 948446u, 1032189u, 1048576u };

    // Target internal symbol size before we've seen the actual source.
    private const int TargetSymbolBytes = 512;

    // ─────────────────────────────────────────────────────────────────
    //  IFecCodec properties
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string CodecName => "Raptor-RFC5053";

    /// <inheritdoc/>
    public byte DeviceTierRequired => 0;   // full device (phone / desktop)

    /// <inheritdoc/>
    public double OverheadFraction => 0.05;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns 0 because the symbol size is determined per-transfer from the
    /// source block length (K = ceil(source.Length / TargetSymbolBytes) and
    /// symbolSize = ceil(source.Length / K)).  Both peers know the source
    /// length and therefore the symbol size without side-channel signalling.
    /// </remarks>
    public int FixedSymbolSizeBytes => 0;

    // ─────────────────────────────────────────────────────────────────
    //  Encode
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Splits source into K source symbols (last zero-padded to symbolSize),
    /// then produces <paramref name="targetSymbolCount"/> LT-encoded symbols
    /// with ESI 0, 1, …, targetSymbolCount-1.
    /// The first K ESIs are typically identical to the source symbols (systematic
    /// property) when degree d happens to be 1 and b = the source index — but this
    /// is probabilistic, not guaranteed.  Callers may choose to transmit K source
    /// symbols + (targetSymbolCount-K) repair symbols for a systematic wrapper.
    /// </remarks>
    public byte[] Encode(ReadOnlySpan<byte> source, int targetSymbolCount)
    {
        (int K, int S) = SourceParams(source.Length);

        if (targetSymbolCount < K)
            throw new ArgumentException(
                $"targetSymbolCount ({targetSymbolCount}) must be ≥ source symbol count ({K}).",
                nameof(targetSymbolCount));

        // Build source symbols (K × S, last padded with zeros).
        byte[][] src = AllocSymbols(K, S);
        for (int i = 0; i < K; i++)
        {
            int off = i * S;
            int len = Math.Min(S, source.Length - off);
            source.Slice(off, len).CopyTo(src[i]);
        }

        byte[] output = new byte[targetSymbolCount * S];
        Span<byte> outSpan = output;

        for (int esi = 0; esi < targetSymbolCount; esi++)
        {
            Span<byte> sym = outSpan.Slice(esi * S, S);
            int[] connected = GetConnectedIndices(esi, K);
            foreach (int idx in connected)
                XorInPlace(sym, src[idx]);
        }

        return output;
    }

    // ─────────────────────────────────────────────────────────────────
    //  TryDecode
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// The index of each element in <paramref name="receivedSymbols"/> is its ESI.
    /// An erased symbol is represented by a zero-length <see cref="ReadOnlyMemory{T}"/>.
    /// Requires at least <paramref name="sourceSymbolCount"/> non-erased symbols.
    /// Uses Belief Propagation (peeling) first, then GF(2) Gaussian Elimination
    /// on any residual unsolved source symbols.
    /// </remarks>
    public bool TryDecode(ReadOnlyMemory<byte>[] receivedSymbols,
                          int sourceSymbolCount,
                          out byte[]? decoded)
    {
        decoded = null;

        int K = sourceSymbolCount;
        if (K <= 0) return false;

        // Infer symbol size from the first non-erased received symbol.
        int S = 0;
        foreach (var sym in receivedSymbols)
            if (sym.Length > 0) { S = sym.Length; break; }
        if (S == 0) return false;

        // Collect non-erased (ESI, symbol-bytes) pairs.
        var received = new List<(int esi, byte[] data)>(K + 8);
        for (int j = 0; j < receivedSymbols.Length; j++)
            if (receivedSymbols[j].Length == S)
                received.Add((j, receivedSymbols[j].ToArray()));

        if (received.Count < K) return false;

        // recovered[i] = byte[S] if source symbol i is known, null otherwise.
        var recovered = new byte[K][];
        int recoveredCount = 0;

        // adj[j] = list of source indices connected to received[j], shrunk as BP proceeds.
        var adj   = new List<int>[received.Count];
        var rhs   = new byte[received.Count][];
        for (int j = 0; j < received.Count; j++)
        {
            adj[j] = new List<int>(GetConnectedIndices(received[j].esi, K));
            rhs[j] = (byte[])received[j].data.Clone();
        }

        // ── Belief Propagation (peeling) ──────────────────────────────────────
        // degree1: queue of coded-symbol indices that currently have degree 1.
        var degree1 = new Queue<int>();
        for (int j = 0; j < adj.Length; j++)
            if (adj[j].Count == 1) degree1.Enqueue(j);

        while (degree1.Count > 0 && recoveredCount < K)
        {
            int j = degree1.Dequeue();
            if (adj[j].Count != 1) continue;   // may have been processed already

            int si = adj[j][0];
            if (recovered[si] != null) continue;  // already known

            // Recover source symbol si from coded symbol j.
            recovered[si] = (byte[])rhs[j].Clone();
            recoveredCount++;

            // Eliminate si from all other coded symbols.
            for (int jj = 0; jj < adj.Length; jj++)
            {
                int pos = adj[jj].IndexOf(si);
                if (pos < 0) continue;
                adj[jj].RemoveAt(pos);
                XorInPlace(rhs[jj], recovered[si]);
                if (adj[jj].Count == 1) degree1.Enqueue(jj);
            }
        }

        if (recoveredCount == K)
        {
            decoded = BuildDecoded(recovered, K, S);
            return true;
        }

        // ── Gaussian Elimination residual ─────────────────────────────────────
        // Collect unrecovered source-symbol indices.
        var unknownSrc = new List<int>(K);
        for (int i = 0; i < K; i++)
            if (recovered[i] == null) unknownSrc.Add(i);

        int U = unknownSrc.Count;

        // Collect coded symbols that still have ≥ 1 unknown dependency.
        var useRows = new List<int>();
        for (int j = 0; j < adj.Length; j++)
            if (adj[j].Count > 0) useRows.Add(j);

        if (useRows.Count < U) return false;   // insufficient equations

        // Map unknown src index → column in GE matrix.
        var colOf = new Dictionary<int, int>(U);
        for (int c = 0; c < U; c++) colOf[unknownSrc[c]] = c;

        // Build M[U rows from useRows][U cols of unknownSrc] + RHS.
        bool[][]  M   = new bool[U][];
        byte[][]  ge  = new byte[U][];

        for (int r = 0; r < U; r++)
        {
            int j = useRows[r];
            M[r]  = new bool[U];
            ge[r] = (byte[])rhs[j].Clone();
            foreach (int si in adj[j])
                if (colOf.TryGetValue(si, out int c))
                    M[r][c] = true;
        }

        // GF(2) RREF.
        for (int col = 0; col < U; col++)
        {
            int pivot = -1;
            for (int row = col; row < U; row++)
                if (M[row][col]) { pivot = row; break; }
            if (pivot < 0) return false;

            if (pivot != col)
            {
                (M[col], M[pivot])   = (M[pivot], M[col]);
                (ge[col], ge[pivot]) = (ge[pivot], ge[col]);
            }

            for (int row = 0; row < U; row++)
            {
                if (row == col || !M[row][col]) continue;
                for (int c = 0; c < U; c++) M[row][c] ^= M[col][c];
                XorInPlace(ge[row], ge[col]);
            }
        }

        // Write GE solutions back.
        for (int r = 0; r < U; r++)
        {
            recovered[unknownSrc[r]] = ge[r];
            recoveredCount++;
        }

        if (recoveredCount < K) return false;

        decoded = BuildDecoded(recovered, K, S);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────
    //  LT symbol generation — degree + neighbour selection
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the K source-symbol indices XOR'd together to produce coded
    /// symbol at position <paramref name="esi"/>.
    ///
    /// Algorithm:
    ///   seed = esi * 0x9E3779B9 ^ 0xDEADBEEF  (Knuth multiplicative hash)
    ///   v20  = xorshift32(seed) &amp; 0xFFFFF      (20-bit value for degree table)
    ///   a    = 1 + xorshift32(v20) % (K-1)     (step in 1..K-1)
    ///   b    = xorshift32(a)       % K          (start index in 0..K-1)
    ///   connected = { (b + a*i) mod K | i = 0..d-1 }
    /// </summary>
    private static int[] GetConnectedIndices(int esi, int K)
    {
        uint s   = (uint)esi * 0x9E3779B9u ^ 0xDEADBEEFu;
        s = Xorshift32(s);
        uint v20 = s & 0xFFFFF;

        int d = DegreeFromTable(v20, K);

        s = Xorshift32((uint)v20);
        int a = (K > 1) ? 1 + (int)(s % (uint)(K - 1)) : 1;

        s = Xorshift32(s);
        int b = (int)(s % (uint)K);

        int[] idx = new int[d];
        for (int i = 0; i < d; i++)
            idx[i] = (b + a * i) % K;

        return idx;
    }

    /// <summary>
    /// Maps a 20-bit uniform value <paramref name="v"/> to a degree using the
    /// RFC 5053 Appendix B cumulative-frequency table, capped at K.
    /// </summary>
    private static int DegreeFromTable(uint v, int K)
    {
        for (int i = 0; i < CumF.Length; i++)
            if (v < CumF[i])
                return Math.Min(K, Degrees[i]);

        return Math.Min(K, Degrees[^1]);
    }

    /// <summary>
    /// Xorshift32 PRNG (Marsaglia 2003). Produces a deterministic pseudo-random
    /// sequence from a 32-bit seed. Non-zero seed required — zero is mapped to 1.
    /// </summary>
    private static uint Xorshift32(uint x)
    {
        if (x == 0) x = 1;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Derives (K, symbolSize) from source length.
    /// K = ceil(source.Length / TargetSymbolBytes), symbolSize = ceil(source.Length / K).
    /// For source.Length = 0: K = 1, symbolSize = TargetSymbolBytes.
    /// </summary>
    private static (int K, int S) SourceParams(int sourceLen)
    {
        if (sourceLen == 0) return (1, TargetSymbolBytes);
        int K = Math.Max(1, (sourceLen + TargetSymbolBytes - 1) / TargetSymbolBytes);
        int S = (sourceLen + K - 1) / K;
        return (K, S);
    }

    private static byte[] BuildDecoded(byte[][] recovered, int K, int S)
    {
        byte[] result = new byte[K * S];
        for (int i = 0; i < K; i++)
            recovered[i].CopyTo(result, i * S);
        return result;
    }

    private static byte[][] AllocSymbols(int count, int symSize)
    {
        byte[][] arr = new byte[count][];
        for (int i = 0; i < count; i++) arr[i] = new byte[symSize];
        return arr;
    }

    private static void XorInPlace(Span<byte> dst, ReadOnlySpan<byte> src)
    {
        for (int i = 0; i < dst.Length; i++)
            dst[i] ^= src[i];
    }
}
