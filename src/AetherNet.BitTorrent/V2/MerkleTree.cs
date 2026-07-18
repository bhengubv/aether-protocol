// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace AetherNet.BitTorrent.V2;

/// <summary>
/// SHA-256 merkle-tree hashing for BitTorrent v2 (BEP-52). A file's data is split into 16 KiB leaf
/// blocks, each SHA-256'd; the leaf layer is zero-padded (a "zero hash" is 32 zero bytes) up to the
/// next power of two; internal nodes are <c>SHA-256(left || right)</c>; the root is the file's
/// "pieces root".
/// </summary>
public static class MerkleTree
{
    public const int BlockSize = 16384;

    public static byte[] ComputeRoot(ReadOnlySpan<byte> data, int blockSize = BlockSize)
    {
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));

        var leaves = new List<byte[]>();
        for (int i = 0; i < data.Length; i += blockSize)
        {
            int len = Math.Min(blockSize, data.Length - i);
            leaves.Add(SHA256.HashData(data.Slice(i, len)));
        }
        if (leaves.Count == 0) return new byte[32]; // empty content → zero root
        return RootOf(leaves);
    }

    /// <summary>Combine leaf hashes into a merkle root, zero-padding to the next power of two.</summary>
    public static byte[] RootOf(IReadOnlyList<byte[]> leafHashes)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);
        if (leafHashes.Count == 0) return new byte[32];

        var level = new List<byte[]>(leafHashes);
        int width = 1;
        while (width < level.Count) width <<= 1;
        var zero = new byte[32];
        while (level.Count < width) level.Add(zero);

        while (level.Count > 1)
        {
            var next = new List<byte[]>(level.Count / 2);
            for (int i = 0; i < level.Count; i += 2)
            {
                var combined = new byte[64];
                level[i].CopyTo(combined, 0);
                level[i + 1].CopyTo(combined, 32);
                next.Add(SHA256.HashData(combined));
            }
            level = next;
        }
        return level[0];
    }
}

/// <summary>BitTorrent v2 (BEP-52) info-hash: SHA-256 of the bencoded info dictionary.</summary>
public static class BitTorrentV2
{
    /// <summary>The full 32-byte v2 info-hash.</summary>
    public static byte[] InfoHash(ReadOnlySpan<byte> infoDictionaryBytes) => SHA256.HashData(infoDictionaryBytes);

    /// <summary>The v2 info-hash truncated to 20 bytes (used where a 20-byte hash is required, e.g. the DHT).</summary>
    public static byte[] InfoHashTruncated(ReadOnlySpan<byte> infoDictionaryBytes) => InfoHash(infoDictionaryBytes)[..20];
}
