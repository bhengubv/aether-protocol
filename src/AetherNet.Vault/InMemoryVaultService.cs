// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Security.Cryptography;
using AetherNet.Vault.Models;

namespace AetherNet.Vault;

/// <summary>
/// In-memory <see cref="IVaultService"/> implementation for testing and
/// single-node scenarios.
///
/// Shard encoding uses real systematic Cauchy-Reed-Solomon over GF(2⁸)
/// (<see cref="ReedSolomonCodec"/>, K=10 / M=4 → N=14): the K data shards are the
/// plaintext partitioned into equal zero-padded slices, the M parity shards are
/// MDS Reed-Solomon, so ANY K of the N shards reconstruct the original. The
/// GF(256) field (primitive polynomial 0x11D) is byte-identical to the rest of the
/// AetherNet FEC stack, so a shard set produced here is decodable by any other node
/// on the mesh.
/// </summary>
public sealed class InMemoryVaultService : IVaultService
{
    private readonly ConcurrentDictionary<string, byte[]> _shards = new();

    // In-memory stub: ShardRequested is never raised because there are no remote
    // peers to request shards. Suppress CS0067 (event never used) intentionally.
#pragma warning disable CS0067
    /// <inheritdoc/>
    public event EventHandler<VaultShardRequest>? ShardRequested;
#pragma warning restore CS0067

    event EventHandler<VaultShardRequest> IVaultService.ShardRequested
    {
        add    => ShardRequested += value;
        remove => ShardRequested -= value;
    }

    /// <inheritdoc/>
    public async Task<VaultManifest> StoreAsync(
        Stream file,
        string label,
        CancellationToken ct = default)
    {
        // Read all bytes.
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var plaintext = ms.ToArray();

        // Content hash for integrity verification.
        var contentHash = ComputeSha256Hex(plaintext);

        const int k = 10;
        const int m = 4;
        int total  = k + m;
        long size  = plaintext.Length;

        // Shard size: ceil(len / k), padded so all data shards are equal length. This slicing is the
        // interop contract — it must stay byte-identical to every other Vault implementation on the mesh.
        int shardSize = size == 0 ? 1 : (int)Math.Ceiling((double)size / k);

        // Build the K systematic data shards (zero-padded slices of the plaintext).
        var dataShards = new byte[k][];
        for (int i = 0; i < k; i++)
        {
            var shard = new byte[shardSize];
            int srcOffset = i * shardSize;
            int copyLen   = (int)Math.Min(shardSize, size - srcOffset);
            if (copyLen > 0)
                Buffer.BlockCopy(plaintext, srcOffset, shard, 0, copyLen);
            dataShards[i] = shard;
        }

        // Real Cauchy-Reed-Solomon: shards 0..K-1 are the data shards unchanged, K..N-1 are the MDS
        // parity shards. Any K of the N reconstruct the original.
        var codec  = new ReedSolomonCodec(k, m);
        var shards = codec.Encode(dataShards);

        var shardHashes = new string[total];
        for (int i = 0; i < total; i++)
        {
            var hash = ComputeSha256Hex(shards[i]);
            shardHashes[i] = hash;
            _shards[hash]  = shards[i];
        }

        var manifest = new VaultManifest
        {
            ContentHash     = contentHash,
            EncryptionSalt  = RandomNumberGenerator.GetBytes(32),
            ShardHashes     = shardHashes,
            K               = k,
            M               = m,
            SizeBytes       = size,
            Label           = label,
            CreatedAtUtc    = DateTime.UtcNow,
        };

        return manifest;
    }

    /// <inheritdoc/>
    public Task<Stream> RecoverAsync(VaultManifest manifest, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        int total = manifest.ShardHashes.Length;
        int k     = manifest.K;
        int m     = total - k;

        // Collect every locally-available shard, keyed by its position (index) in the manifest. The
        // index is what the codec needs to know which generator row each surviving shard came from.
        var available = new Dictionary<int, byte[]>();
        for (int i = 0; i < total; i++)
        {
            if (_shards.TryGetValue(manifest.ShardHashes[i], out var shard))
                available[i] = shard;
        }

        if (available.Count < k)
            throw new InvalidOperationException(
                $"Cannot recover: only {available.Count}/{k} shards available.");

        // Reconstruct the K data shards from ANY K survivors (data, parity, or a mix) via Reed-Solomon.
        var codec = new ReedSolomonCodec(k, m);
        byte[][] dataShards = codec.DecodeDataShards(available);

        // Concatenate the K data shards in order, then trim to the original size.
        using var buffer = new MemoryStream();
        foreach (var shard in dataShards)
            buffer.Write(shard);

        var result = buffer.ToArray()[..(int)manifest.SizeBytes];
        return Task.FromResult<Stream>(new MemoryStream(result));
    }

    /// <inheritdoc/>
    public Task<VaultHealth> CheckHealthAsync(VaultManifest manifest, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        int reachable = manifest.ShardHashes.Count(h => _shards.ContainsKey(h));

        var health = new VaultHealth
        {
            TotalShards     = manifest.TotalShards,
            ReachableShards = reachable,
            IsRecoverable   = reachable >= manifest.K,
            RedundancyScore = manifest.TotalShards > 0
                ? (double)reachable / manifest.TotalShards
                : 0.0,
        };

        return Task.FromResult(health);
    }

    /// <inheritdoc/>
    public Task ReplicateAsync(VaultManifest manifest, int targetRedundancy = 14, CancellationToken ct = default)
    {
        // No-op in the in-memory implementation.
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeSha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Removes a set of shards from the local store (for testing).</summary>
    internal void RemoveShards(IEnumerable<string> shardHashes)
    {
        foreach (var h in shardHashes)
            _shards.TryRemove(h, out _);
    }
}
