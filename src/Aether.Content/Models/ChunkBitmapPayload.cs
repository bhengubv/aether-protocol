// SPDX-License-Identifier: MIT

namespace Aether.Content.Models;

/// <summary>
/// Cross-language stable wire payload for <see cref="Aether.Protocol.PacketType.ChunkBitmap"/>.
///
/// <para>
/// A node broadcasts this packet to announce which chunks of a particular content
/// it currently holds. Receivers use the information to make non-overlapping,
/// randomised pull requests (the Chunk Shuffle / Self-Assembling Peer Interleaving
/// protocol), so each peer pulls a disjoint subset of chunks in parallel without
/// coordination overhead.
/// </para>
///
/// <para>
/// Wire shape: JSON, snake_case. Cross-language stable — all eight language ports
/// MUST produce and consume identical JSON for this payload.
/// </para>
/// </summary>
public sealed class ChunkBitmapPayload
{
    /// <summary>Root hash identifying the content (hex-encoded SHA-256, lowercase).</summary>
    public string RootHash { get; set; } = string.Empty;

    /// <summary>
    /// Total number of chunks in the content. Needed by the receiver to correctly
    /// interpret the trailing bits of <see cref="HaveBitset"/> and to detect stale
    /// payloads from a previous publish of the same root hash.
    /// </summary>
    public int ChunkCount { get; set; }

    /// <summary>
    /// Compact bit-array encoding which chunks this node holds.
    ///
    /// <para>
    /// Encoding: bit <c>i</c> (LSB-first within each byte) is set when chunk <c>i</c>
    /// is present. Length is <c>ceil(<see cref="ChunkCount"/> / 8)</c>. Trailing bits
    /// in the last byte beyond <see cref="ChunkCount"/> MUST be zero.
    /// </para>
    ///
    /// <para>
    /// Cross-language note: JSON serialises <c>byte[]</c> as Base64. All language
    /// implementations MUST use Base64 encoding for this field on the wire and MUST
    /// accept both padded and unpadded Base64 on receive.
    /// </para>
    /// </summary>
    public byte[] HaveBitset { get; set; } = [];

    /// <summary>
    /// Monotonically increasing generation counter. Each node increments this on
    /// every bitmap broadcast for the same root hash. Receivers MUST discard payloads
    /// whose <see cref="Generation"/> is less than or equal to the latest generation
    /// already seen from the same sender for the same root hash, preventing
    /// out-of-order processing.
    ///
    /// <para>
    /// Wraps at <see cref="uint.MaxValue"/> → 0. No session runs long enough for
    /// this to cause a correctness issue in practice.
    /// </para>
    /// </summary>
    public uint Generation { get; set; }

    // ── Encoding helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Encode a boolean have-flag array into the compact bitset representation.
    /// Bit <c>i</c> is the LSB-first bit within byte <c>i / 8</c>.
    /// </summary>
    public static byte[] Encode(bool[] haveFlags)
    {
        if (haveFlags.Length == 0) return [];
        var byteCount = (haveFlags.Length + 7) / 8;
        var result = new byte[byteCount];
        for (var i = 0; i < haveFlags.Length; i++)
            if (haveFlags[i]) result[i >> 3] |= (byte)(1 << (i & 7));
        return result;
    }

    /// <summary>
    /// Decode a compact bitset into a boolean array of exactly <paramref name="chunkCount"/> elements.
    /// Bits beyond the <paramref name="chunkCount"/> limit are ignored.
    /// </summary>
    public static bool[] Decode(byte[] bitset, int chunkCount)
    {
        ArgumentNullException.ThrowIfNull(bitset);
        if (chunkCount <= 0) return [];
        var result = new bool[chunkCount];
        var limit = Math.Min(chunkCount, bitset.Length * 8);
        for (var i = 0; i < limit; i++)
            result[i] = (bitset[i >> 3] & (1 << (i & 7))) != 0;
        return result;
    }

    /// <summary>
    /// Returns <c>true</c> when every bit in [0, <see cref="ChunkCount"/>) is set,
    /// indicating a complete seeder.
    /// </summary>
    public bool HasAll()
    {
        if (ChunkCount == 0) return true;
        var flags = Decode(HaveBitset, ChunkCount);
        foreach (var f in flags)
            if (!f) return false;
        return true;
    }
}
