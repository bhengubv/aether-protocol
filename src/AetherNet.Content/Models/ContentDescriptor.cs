// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace AetherNet.Content.Models;

/// <summary>
/// Manifest for a piece of chunked content. Identifies the content by a root hash
/// computed over the per-chunk hashes, declares the chunk layout, and lets
/// receivers verify each chunk independently as it arrives.
///
/// Wire shape (JSON, snake_case): cross-language stable. Producers can publish a
/// descriptor once and any node can pull chunks and verify against it without
/// trusting the sender — content addressing makes the descriptor itself the
/// authority.
/// </summary>
public sealed class ContentDescriptor
{
    /// <summary>SHA-256 over the concatenation of all chunk hashes, in order. Hex-encoded lowercase.</summary>
    public string RootHash { get; set; } = string.Empty;

    /// <summary>Original file name as the publisher named it. Hint only — never used as a path on the receiver.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Total size of the original content in bytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Bytes per chunk for every chunk except possibly the last.</summary>
    public int ChunkSizeBytes { get; set; } = AetherNet.Constants.ProtocolConstants.DefaultChunkSizeBytes;

    /// <summary>Total number of chunks. Equal to ceil(<see cref="TotalBytes"/> / <see cref="ChunkSizeBytes"/>).</summary>
    public int ChunkCount { get; set; }

    /// <summary>SHA-256 of each chunk's bytes, in chunk-index order. Hex-encoded lowercase.</summary>
    public IReadOnlyList<string> ChunkHashes { get; set; } = Array.Empty<string>();

    /// <summary>Caller-defined MIME type or media kind. Opaque to the protocol.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>UTC creation time of the descriptor.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Build a descriptor from a buffer. Splits into <paramref name="chunkSizeBytes"/>-sized
    /// chunks (except the trailing chunk, which may be smaller), hashes each, and computes
    /// the root over the chunk-hash concatenation.
    /// </summary>
    public static ContentDescriptor FromBytes(string name, ReadOnlySpan<byte> data, string contentType = "application/octet-stream", int chunkSizeBytes = 0)
    {
        if (chunkSizeBytes <= 0) chunkSizeBytes = AetherNet.Constants.ProtocolConstants.DefaultChunkSizeBytes;
        var chunkCount = (int)((data.Length + chunkSizeBytes - 1) / (long)chunkSizeBytes);
        var hashes = new string[chunkCount];

        Span<byte> chunkHashBytes = stackalloc byte[32];
        var concat = new byte[chunkCount * 32];

        for (var i = 0; i < chunkCount; i++)
        {
            var start = (long)i * chunkSizeBytes;
            var len = (int)Math.Min(chunkSizeBytes, data.Length - start);
            var chunk = data.Slice((int)start, len);
            if (!SHA256.TryHashData(chunk, chunkHashBytes, out _))
                throw new InvalidOperationException("SHA-256 chunk hash failed");
            hashes[i] = Convert.ToHexString(chunkHashBytes).ToLowerInvariant();
            chunkHashBytes.CopyTo(concat.AsSpan(i * 32, 32));
        }

        Span<byte> rootHashBytes = stackalloc byte[32];
        if (!SHA256.TryHashData(concat, rootHashBytes, out _))
            throw new InvalidOperationException("SHA-256 root hash failed");

        return new ContentDescriptor
        {
            RootHash = Convert.ToHexString(rootHashBytes).ToLowerInvariant(),
            Name = name,
            TotalBytes = data.Length,
            ChunkSizeBytes = chunkSizeBytes,
            ChunkCount = chunkCount,
            ChunkHashes = hashes,
            ContentType = contentType,
        };
    }

    /// <summary>Verify a chunk by recomputing its SHA-256 and comparing to <see cref="ChunkHashes"/>[index].</summary>
    public bool VerifyChunk(int chunkIndex, ReadOnlySpan<byte> chunkBytes)
    {
        if (chunkIndex < 0 || chunkIndex >= ChunkHashes.Count) return false;

        Span<byte> hash = stackalloc byte[32];
        if (!SHA256.TryHashData(chunkBytes, hash, out _)) return false;
        return string.Equals(Convert.ToHexString(hash).ToLowerInvariant(), ChunkHashes[chunkIndex], StringComparison.Ordinal);
    }

    /// <summary>Recompute the root hash over <see cref="ChunkHashes"/> and compare. Detects manifest tampering.</summary>
    public bool VerifySelf()
    {
        if (ChunkHashes.Count != ChunkCount) return false;
        var concat = new byte[ChunkHashes.Count * 32];
        for (var i = 0; i < ChunkHashes.Count; i++)
        {
            byte[] bytes;
            try { bytes = Convert.FromHexString(ChunkHashes[i]); }
            catch (FormatException) { return false; }
            if (bytes.Length != 32) return false;
            Buffer.BlockCopy(bytes, 0, concat, i * 32, 32);
        }
        Span<byte> root = stackalloc byte[32];
        if (!SHA256.TryHashData(concat, root, out _)) return false;
        return string.Equals(Convert.ToHexString(root).ToLowerInvariant(), RootHash, StringComparison.Ordinal);
    }
}
