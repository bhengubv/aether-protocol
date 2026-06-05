// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Security.Cryptography;
using AetherMesh.Vault.Models;

namespace AetherMesh.Vault;

/// <summary>
/// In-memory <see cref="IVaultService"/> implementation for testing and
/// single-node scenarios.
///
/// Shard splitting uses byte partitioning (not Reed-Solomon) — sufficient
/// for unit tests. Production implementations should use libfec/RS.
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

        // Split into K=10 data shards.
        const int k = 10;
        const int m = 4;
        int total  = k + m;
        long size  = plaintext.Length;

        // Shard size: ceil(len / k), padded so all data shards are equal length.
        int shardSize = size == 0 ? 1 : (int)Math.Ceiling((double)size / k);

        var shardHashes = new string[total];

        for (int i = 0; i < total; i++)
        {
            byte[] shard;
            if (i < k)
            {
                // Data shard: slice of the plaintext (zero-padded if last shard is short).
                shard = new byte[shardSize];
                int srcOffset = i * shardSize;
                int copyLen   = (int)Math.Min(shardSize, size - srcOffset);
                if (copyLen > 0)
                    Buffer.BlockCopy(plaintext, srcOffset, shard, 0, copyLen);
            }
            else
            {
                // Parity shard (simulation): XOR of data shards 0…k-1 for shard 0,
                // zero-filled for the rest (simplified — not real RS).
                shard = new byte[shardSize];
                if (i == k)
                {
                    for (int d = 0; d < k; d++)
                    {
                        var dataKey   = shardHashes[d];
                        var dataBytes = _shards[dataKey];
                        for (int b = 0; b < shardSize; b++)
                            shard[b] ^= dataBytes[b];
                    }
                }
            }

            var hash = ComputeSha256Hex(shard);
            shardHashes[i] = hash;
            _shards[hash]  = shard;
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

        // Collect the first K available shards.
        var collected = new List<(int index, byte[] data)>();
        for (int i = 0; i < manifest.ShardHashes.Length && collected.Count < manifest.K; i++)
        {
            if (_shards.TryGetValue(manifest.ShardHashes[i], out var shard))
                collected.Add((i, shard));
        }

        if (collected.Count < manifest.K)
            throw new InvalidOperationException(
                $"Cannot recover: only {collected.Count}/{manifest.K} shards available.");

        // Reassemble from the K data shards (indices 0..K-1 in order).
        var dataShards = collected
            .Where(t => t.index < manifest.K)
            .OrderBy(t => t.index)
            .Select(t => t.data)
            .ToArray();

        // If we have at least K data shards, concatenate them.
        using var buffer = new MemoryStream();
        foreach (var shard in dataShards)
            buffer.Write(shard);

        // Trim to original size.
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
