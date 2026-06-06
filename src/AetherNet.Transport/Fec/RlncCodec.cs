// SPDX-License-Identifier: MIT
// RLNC Engine — Random Linear Network Coding over GF(2⁸).
//
// Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1  (= 0x11D, same as AES Rijndael).
//
// Components
// ──────────
//   GF256       — static GF(2⁸) arithmetic (log/exp tables; constant-time lookups).
//   RlncEncoder — splits source data into K symbols; emits systematic + repair packets.
//   RlncDecoder — incremental Gauss-Jordan elimination over GF(256); decodes on rank = K.
//   RlncCodec   — IFecCodec adapter; bulk encode/decode via the encoder+decoder pair.
//
// Encoding wire format per packet:
//   [ K coefficient bytes ] [ symbolSize data bytes ]
//
// Systematic mode (default ON):
//   The first K packets carry identity-matrix coefficients — they are byte-identical
//   to the K source symbols.  Repair packets use random GF(256) coefficients.
//   Receivers can decode partial data immediately on arrival of any systematic packet.

using System;
using System.Security.Cryptography;
using AetherNet.Transport.Abstractions;

namespace AetherNet.Transport.Fec;

// ── GF(2⁸) arithmetic ─────────────────────────────────────────────────────────

/// <summary>
/// Static GF(2⁸) arithmetic helper.
/// All operations are O(1) constant-time table lookups — no branches on secret data.
/// </summary>
internal static class GF256
{
    /// <summary>
    /// Exp table: Exp[i] = α^i in GF(2⁸).
    /// Doubled to [512] so that Mul can index directly without modular reduction.
    /// Exp[i] = Exp[i − 255] for i ∈ [255, 511].
    /// </summary>
    internal static readonly byte[] Exp = new byte[512];

    /// <summary>
    /// Log table: Log[v] = log_α(v) for v ∈ [1, 255].
    /// Log[0] is undefined (logarithm of zero).
    /// </summary>
    internal static readonly byte[] Log = new byte[256];

    static GF256()
    {
        // Build Exp and Log tables using the primitive polynomial x⁸+x⁴+x³+x²+1 (0x11D).
        // α = 2 (the polynomial "x") is a primitive root of GF(256).
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x]  = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D; // reduce mod p(x)
        }
        // α^255 = 1; wrap the table so Mul can use raw index addition.
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        Log[1] = 0; // log_α(1) = 0  (already set by loop but explicit for clarity)
    }

    /// <summary>Addition in GF(2⁸) = XOR.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static byte Add(byte a, byte b) => (byte)(a ^ b);

    /// <summary>Multiplication in GF(2⁸) using log/exp tables.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static byte Mul(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return Exp[Log[a] + Log[b]];
    }

    /// <summary>Multiplicative inverse in GF(2⁸): Inv(a) = α^(255 − log_α(a)).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static byte Inv(byte a)
    {
        if (a == 0) throw new DivideByZeroException("GF256: inverse of zero");
        return Exp[255 - Log[a]];
    }

    /// <summary>Division: Div(a, b) = Mul(a, Inv(b)).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Mul(a, Inv(b));
}

// ── RlncEncoder ───────────────────────────────────────────────────────────────

/// <summary>
/// Encodes K source symbols using systematic + random-repair packet generation.
/// </summary>
public sealed class RlncEncoder
{
    private readonly byte[][] _source;
    private          int      _nextIndex;

    /// <summary>Number of source symbols in this generation.</summary>
    public int GenerationSize => _source.Length;

    /// <summary>Byte length of each source symbol.</summary>
    public int SymbolSize => _source[0].Length;

    /// <summary>
    /// Whether the first <see cref="GenerationSize"/> packets are systematic
    /// (identity coefficient vectors, equal to the original source symbols).
    /// </summary>
    public bool IsSystematic { get; }

    /// <param name="source">Array of K byte arrays, each symbolSize bytes.</param>
    /// <param name="systematic">
    ///   When <c>true</c> (default), the first K encoded packets carry identity
    ///   coefficients and are byte-identical to the source symbols.
    /// </param>
    public RlncEncoder(byte[][] source, bool systematic = true)
    {
        if (source is null || source.Length == 0)
            throw new ArgumentException("Source must have at least one symbol.", nameof(source));

        _source      = source;
        IsSystematic = systematic;
        _nextIndex   = 0;
    }

    /// <summary>
    /// Produce the next encoded packet.
    /// </summary>
    /// <returns>
    ///   <c>(Coefficients, EncodedSymbol)</c> where Coefficients is a K-byte
    ///   GF(256) vector and EncodedSymbol is the corresponding coded data.
    /// </returns>
    public (byte[] Coefficients, byte[] EncodedSymbol) NextPacket()
    {
        int k = GenerationSize;
        byte[] coefficients;
        byte[] encoded;

        if (IsSystematic && _nextIndex < k)
        {
            // Systematic: e_i = standard basis vector.
            coefficients              = new byte[k];
            coefficients[_nextIndex]  = 1;
            encoded                   = (byte[])_source[_nextIndex].Clone();
        }
        else
        {
            // Repair: random GF(256) coefficient vector.
            coefficients = new byte[k];
            RandomNumberGenerator.Fill(coefficients);
            // Extremely unlikely but guard against all-zero vector.
            bool allZero = true;
            foreach (byte c in coefficients) { if (c != 0) { allZero = false; break; } }
            if (allZero) coefficients[0] = 1;

            encoded = EncodeSymbol(coefficients);
        }

        _nextIndex++;
        return (coefficients, encoded);
    }

    private byte[] EncodeSymbol(byte[] coefficients)
    {
        int s = SymbolSize;
        var output = new byte[s];
        for (int k = 0; k < _source.Length; k++)
        {
            byte c = coefficients[k];
            if (c == 0) continue;
            byte[] src = _source[k];
            for (int i = 0; i < s; i++)
                output[i] = GF256.Add(output[i], GF256.Mul(c, src[i]));
        }
        return output;
    }
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

/// <summary>
/// Incremental Gauss-Jordan decoder over GF(2⁸).
///
/// Maintains the accumulated coefficient matrix in reduced row echelon form
/// (RREF) as packets arrive.  Decoding is immediate when rank equals K:
/// the B portion of the augmented matrix [A|B] directly yields the source symbols.
/// </summary>
public sealed class RlncDecoder
{
    private readonly int      _k;
    private readonly int      _symbolSize;

    // _pivotCoeff[j] = the unique normalised row whose pivot is at column j.
    // _pivotData[j]  = the corresponding symbol bytes for that row.
    // Null slots indicate columns without a pivot yet.
    private readonly byte[]?[] _pivotCoeff;
    private readonly byte[]?[] _pivotData;

    /// <summary>Number of linearly independent packets received so far.</summary>
    public int Rank { get; private set; }

    /// <summary>Whether K independent packets have been received.</summary>
    public bool IsComplete => Rank == _k;

    /// <param name="k">Generation size (number of source symbols).</param>
    /// <param name="symbolSize">Byte length of each symbol.</param>
    public RlncDecoder(int k, int symbolSize)
    {
        _k          = k;
        _symbolSize = symbolSize;
        _pivotCoeff = new byte[k][];
        _pivotData  = new byte[k][];
    }

    /// <summary>
    /// Submit an encoded packet.
    /// </summary>
    /// <param name="coefficients">K-byte GF(256) coefficient vector.</param>
    /// <param name="encodedSymbol">symbolSize-byte encoded data.</param>
    /// <returns><c>true</c> if this packet increased the decoder's rank.</returns>
    public bool AddPacket(ReadOnlySpan<byte> coefficients, ReadOnlySpan<byte> encodedSymbol)
    {
        // Work on mutable copies.
        byte[] row  = coefficients.ToArray();
        byte[] data = encodedSymbol.ToArray();

        // ── Forward-elimination: reduce against all existing pivot rows ──────
        for (int j = 0; j < _k; j++)
        {
            if (row[j] == 0) continue;
            byte[]? pr = _pivotCoeff[j];
            if (pr is null) continue; // no pivot in column j yet

            byte c  = row[j];
            byte[] pd = _pivotData[j]!;

            for (int i = 0; i < _k; i++)
                row[i] = GF256.Add(row[i], GF256.Mul(c, pr[i]));
            for (int i = 0; i < _symbolSize; i++)
                data[i] = GF256.Add(data[i], GF256.Mul(c, pd[i]));
        }

        // ── Find leftmost non-zero (pivot column) ────────────────────────────
        int pivotCol = -1;
        for (int j = 0; j < _k; j++)
        {
            if (row[j] != 0) { pivotCol = j; break; }
        }
        if (pivotCol < 0) return false; // linearly dependent — discard

        // ── Normalise: scale row so pivot element = 1 ─────────────────────────
        byte inv = GF256.Inv(row[pivotCol]);
        for (int i = 0; i < _k;          i++) row[i]  = GF256.Mul(inv, row[i]);
        for (int i = 0; i < _symbolSize; i++) data[i] = GF256.Mul(inv, data[i]);

        // ── Back-substitution: eliminate pivot column from all other rows ─────
        for (int r = 0; r < _k; r++)
        {
            byte[]? pr = _pivotCoeff[r];
            if (pr is null) continue;
            byte c = pr[pivotCol];
            if (c == 0) continue;

            byte[] pd = _pivotData[r]!;
            for (int i = 0; i < _k;          i++) pr[i] = GF256.Add(pr[i], GF256.Mul(c, row[i]));
            for (int i = 0; i < _symbolSize; i++) pd[i] = GF256.Add(pd[i], GF256.Mul(c, data[i]));
        }

        _pivotCoeff[pivotCol] = row;
        _pivotData[pivotCol]  = data;
        Rank++;
        return true;
    }

    /// <summary>
    /// Returns the concatenated decoded source symbols when rank = K,
    /// or <c>null</c> if more packets are still needed.
    /// </summary>
    public byte[]? TryDecode()
    {
        if (!IsComplete) return null;

        // After RREF, _pivotData[j] = source symbol j (pivot at column j means row = e_j).
        var result = new byte[_k * _symbolSize];
        for (int j = 0; j < _k; j++)
            _pivotData[j]!.CopyTo(result, j * _symbolSize);
        return result;
    }
}

// ── RlncCodec : IFecCodec ─────────────────────────────────────────────────────

/// <summary>
/// <see cref="IFecCodec"/> adapter for RLNC over GF(2⁸).
///
/// Each encoded packet is:  [ K coefficient bytes ][ symbolSize data bytes ]
///
/// <see cref="Encode"/> produces <c>targetSymbolCount</c> concatenated packets.
/// <see cref="TryDecode"/> reconstructs the original data from any K independent packets.
/// </summary>
public sealed class RlncCodec : IFecCodec
{
    private readonly int _k; // generation size (source symbols per generation)

    /// <param name="generationSize">
    ///   Number of source symbols per generation.  Values between 8 and 64 are
    ///   typical; larger values improve coding efficiency at the cost of decoding
    ///   latency and coefficient header overhead.
    /// </param>
    public RlncCodec(int generationSize = 16)
    {
        if (generationSize < 1 || generationSize > 255)
            throw new ArgumentOutOfRangeException(nameof(generationSize), "Must be in [1, 255].");
        _k = generationSize;
    }

    /// <inheritdoc/>
    public string CodecName => "RLNC-GF256";

    /// <inheritdoc/>
    public byte DeviceTierRequired => 0; // Runs on all device tiers.

    /// <inheritdoc/>
    /// <remarks>
    /// Expressed as the ratio of coefficient-header bytes to payload bytes.
    /// For K=16 and symbolSize=512: overhead = 16/512 ≈ 3.1 %.
    /// </remarks>
    public double OverheadFraction => 0.05; // nominal 5 % for typical K=16 / S=512

    /// <inheritdoc/>
    public int FixedSymbolSizeBytes => 0; // variable-length (caller-determined)

    /// <inheritdoc/>
    /// <remarks>
    /// Wire format: each of <paramref name="targetSymbolCount"/> packets is
    /// <c>[ K coefficient bytes ][ symbolSize data bytes ]</c>, concatenated.
    ///
    /// The first K packets are systematic (equal to the source symbols).
    /// Subsequent packets are random linear combinations (repair symbols).
    /// </remarks>
    public byte[] Encode(ReadOnlySpan<byte> source, int targetSymbolCount)
    {
        if (source.IsEmpty)
            throw new ArgumentException("Source must not be empty.", nameof(source));

        int symbolSize = (source.Length + _k - 1) / _k;
        byte[][] symbols = SplitIntoSymbols(source, symbolSize);
        int packetSize   = _k + symbolSize;

        var encoder = new RlncEncoder(symbols, systematic: true);
        var output  = new byte[targetSymbolCount * packetSize];

        for (int i = 0; i < targetSymbolCount; i++)
        {
            var (coeff, data) = encoder.NextPacket();
            int offset        = i * packetSize;
            coeff.CopyTo(output, offset);
            data.CopyTo(output, offset + _k);
        }
        return output;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Each element of <paramref name="receivedSymbols"/> must be
    /// <c>[ K coefficient bytes ][ symbolSize data bytes ]</c> as produced by
    /// <see cref="Encode"/>.  Returns <c>true</c> and sets <paramref name="decoded"/>
    /// when at least K linearly independent packets have been provided.
    /// </remarks>
    public bool TryDecode(
        ReadOnlyMemory<byte>[] receivedSymbols,
        int sourceSymbolCount,
        out byte[]? decoded)
    {
        decoded = null;
        if (receivedSymbols is null || receivedSymbols.Length == 0) return false;

        int packetSize = receivedSymbols[0].Length;
        int symbolSize = packetSize - _k;
        if (symbolSize <= 0) return false;

        var decoder = new RlncDecoder(_k, symbolSize);

        foreach (var pkt in receivedSymbols)
        {
            var span = pkt.Span;
            decoder.AddPacket(span[.._k], span[_k..]);
            if (decoder.IsComplete) break;
        }

        decoded = decoder.TryDecode();
        return decoded is not null;
    }

    private byte[][] SplitIntoSymbols(ReadOnlySpan<byte> source, int symbolSize)
    {
        var symbols = new byte[_k][];
        for (int i = 0; i < _k; i++)
        {
            symbols[i]  = new byte[symbolSize];
            int offset  = i * symbolSize;
            int length  = Math.Min(symbolSize, source.Length - offset);
            if (length > 0) source.Slice(offset, length).CopyTo(symbols[i]);
        }
        return symbols;
    }
}
