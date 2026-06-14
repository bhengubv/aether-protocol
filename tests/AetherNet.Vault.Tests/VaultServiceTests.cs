// SPDX-License-Identifier: MIT
using AetherNet.Vault;
using AetherNet.Vault.Models;
using Xunit;

namespace AetherNet.Vault.Tests;

public sealed class VaultServiceTests
{
    // ── 1. StoreAsync returns a manifest with K+M=14 shards ──────────────────

    [Fact]
    public async Task StoreAsync_ReturnsManifestWithCorrectShardCount()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[1000];
        Random.Shared.NextBytes(data);

        var manifest = await svc.StoreAsync(new MemoryStream(data), "test");

        Assert.Equal(14, manifest.ShardHashes.Length);
        Assert.Equal(10, manifest.K);
        Assert.Equal(4,  manifest.M);
        Assert.Equal(14, manifest.TotalShards);
    }

    // ── 2. StoreAsync sets SizeBytes to the original file size ───────────────

    [Fact]
    public async Task StoreAsync_SetsCorrectSizeBytes()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[2500];
        Random.Shared.NextBytes(data);

        var manifest = await svc.StoreAsync(new MemoryStream(data), "label");

        Assert.Equal(2500L, manifest.SizeBytes);
    }

    // ── 3. StoreAsync sets the Label ─────────────────────────────────────────

    [Fact]
    public async Task StoreAsync_SetsLabel()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[100];

        var manifest = await svc.StoreAsync(new MemoryStream(data), "my-label");

        Assert.Equal("my-label", manifest.Label);
    }

    // ── 4. StoreAsync sets a non-empty ContentHash ───────────────────────────

    [Fact]
    public async Task StoreAsync_SetsContentHash_NonEmpty()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[512];
        Random.Shared.NextBytes(data);

        var manifest = await svc.StoreAsync(new MemoryStream(data), "hash-check");

        Assert.NotEmpty(manifest.ContentHash);
        // SHA-256 hex is 64 chars
        Assert.Equal(64, manifest.ContentHash.Length);
    }

    // ── 5. RecoverAsync returns the original bytes ───────────────────────────

    [Fact]
    public async Task RecoverAsync_RecoversOriginalBytes()
    {
        var svc = new InMemoryVaultService();
        var original = new byte[3000];
        Random.Shared.NextBytes(original);

        var manifest = await svc.StoreAsync(new MemoryStream(original), "recovery");
        var recovered = await svc.RecoverAsync(manifest);

        using var ms = new MemoryStream();
        await recovered.CopyToAsync(ms);
        var result = ms.ToArray();

        Assert.Equal(original, result);
    }

    // ── 6. RecoverAsync throws when fewer than K shards available ────────────

    [Fact]
    public async Task RecoverAsync_ThrowsWhenInsufficientShards()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[1000];
        Random.Shared.NextBytes(data);

        var manifest = await svc.StoreAsync(new MemoryStream(data), "insufficient");

        // Remove K+1 = 11 shards — only 3 data shards remain (fewer than K=10).
        svc.RemoveShards(manifest.ShardHashes.Take(11));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecoverAsync(manifest));
    }

    // ── 7. CheckHealthAsync — all shards present — IsRecoverable = true ──────

    [Fact]
    public async Task CheckHealthAsync_AllShardsReachable_IsRecoverableTrue()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[500];

        var manifest = await svc.StoreAsync(new MemoryStream(data), "health-all");
        var health = await svc.CheckHealthAsync(manifest);

        Assert.Equal(14, health.TotalShards);
        Assert.Equal(14, health.ReachableShards);
        Assert.True(health.IsRecoverable);
        Assert.Equal(1.0, health.RedundancyScore, precision: 5);
    }

    // ── 8. CheckHealthAsync — M shards removed — K remain — still recoverable

    [Fact]
    public async Task CheckHealthAsync_EnoughShards_IsRecoverableTrue()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[800];
        Random.Shared.NextBytes(data);

        var manifest = await svc.StoreAsync(new MemoryStream(data), "health-enough");

        // Remove exactly M=4 shards — K=10 remain.
        svc.RemoveShards(manifest.ShardHashes.TakeLast(4));

        var health = await svc.CheckHealthAsync(manifest);

        Assert.Equal(10, health.ReachableShards);
        Assert.True(health.IsRecoverable);
    }

    // ── 9. CheckHealthAsync — too few shards — IsRecoverable = false ─────────

    [Fact]
    public async Task CheckHealthAsync_TooFewShards_IsRecoverableFalse()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[600];
        Random.Shared.NextBytes(data);

        var manifest = await svc.StoreAsync(new MemoryStream(data), "health-too-few");

        // Remove K+1=11 shards — only 3 remain (fewer than K=10).
        svc.RemoveShards(manifest.ShardHashes.Take(11));

        var health = await svc.CheckHealthAsync(manifest);

        Assert.Equal(3, health.ReachableShards);
        Assert.False(health.IsRecoverable);
    }

    // ── 10. CheckHealthAsync — RedundancyScore is correct fraction ───────────

    [Fact]
    public async Task CheckHealthAsync_RedundancyScore_IsCorrectFraction()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[700];
        Random.Shared.NextBytes(data);

        var manifest = await svc.StoreAsync(new MemoryStream(data), "health-score");

        // Remove 7 shards — 7 of 14 remain → score = 0.5
        svc.RemoveShards(manifest.ShardHashes.Take(7));

        var health = await svc.CheckHealthAsync(manifest);

        Assert.Equal(7, health.ReachableShards);
        Assert.Equal(0.5, health.RedundancyScore, precision: 5);
    }

    // ── 11. ReplicateAsync is a no-op — does not throw ───────────────────────

    [Fact]
    public async Task ReplicateAsync_IsNoOp_DoesNotThrow()
    {
        var svc = new InMemoryVaultService();
        var data = new byte[200];

        var manifest = await svc.StoreAsync(new MemoryStream(data), "replicate");

        // Should complete without throwing.
        await svc.ReplicateAsync(manifest, targetRedundancy: 14);
    }

    // ── 12. StoreAsync with empty stream — SizeBytes = 0 ─────────────────────

    [Fact]
    public async Task StoreAsync_EmptyStream_ReturnsManifestWithZeroSizeBytes()
    {
        var svc = new InMemoryVaultService();

        var manifest = await svc.StoreAsync(new MemoryStream(Array.Empty<byte>()), "empty");

        Assert.Equal(0L, manifest.SizeBytes);
        Assert.Equal(14, manifest.ShardHashes.Length);
    }

    // ── 13. Empty file round-trips to empty (recovers cleanly) ───────────────

    [Fact]
    public async Task RecoverAsync_EmptyFile_RoundTripsToEmpty()
    {
        var svc = new InMemoryVaultService();

        var manifest = await svc.StoreAsync(new MemoryStream(Array.Empty<byte>()), "empty");
        var recovered = await svc.RecoverAsync(manifest);

        using var ms = new MemoryStream();
        await recovered.CopyToAsync(ms);
        Assert.Empty(ms.ToArray());
    }

    // ── 14. REAL RS: drop M=4 PARITY shards — K data shards remain — recovers ─

    [Fact]
    public async Task RecoverAsync_AfterDroppingM_ParityShards_StillRecovers()
    {
        var svc = new InMemoryVaultService();
        var original = new byte[2222];
        Random.Shared.NextBytes(original);

        var manifest = await svc.StoreAsync(new MemoryStream(original), "drop-parity");

        // Drop exactly M=4 shards — the LAST 4 (the parity shards). K=10 data shards remain.
        svc.RemoveShards(manifest.ShardHashes.TakeLast(manifest.M));

        var recovered = await svc.RecoverAsync(manifest);
        using var ms = new MemoryStream();
        await recovered.CopyToAsync(ms);

        Assert.Equal(original, ms.ToArray());
    }

    // ── 15. REAL RS: drop M=4 DATA shards — recover VIA PARITY (matrix inv) ───
    //
    // This is the case ONLY genuine Reed-Solomon can pass: dropping data shards forces recovery to use
    // the parity shards through Gauss-Jordan matrix inversion. A byte-partition simulation cannot do it.

    [Fact]
    public async Task RecoverAsync_AfterDroppingM_DataShards_RecoversViaParity()
    {
        var svc = new InMemoryVaultService();
        var original = new byte[5000];
        Random.Shared.NextBytes(original);

        var manifest = await svc.StoreAsync(new MemoryStream(original), "drop-data");

        // Drop the FIRST 4 shards (data shards 0..3). 6 data + 4 parity = 10 = K remain → RS reconstructs.
        svc.RemoveShards(manifest.ShardHashes.Take(manifest.M));

        var recovered = await svc.RecoverAsync(manifest);
        using var ms = new MemoryStream();
        await recovered.CopyToAsync(ms);

        Assert.Equal(original, ms.ToArray());
    }

    // ── 16. REAL RS: any K-of-N (data+parity mix) reconstructs ───────────────

    [Fact]
    public async Task RecoverAsync_FromDataParityMix_OfExactlyK_Recovers()
    {
        var svc = new InMemoryVaultService();
        var original = new byte[4096];
        Random.Shared.NextBytes(original);

        var manifest = await svc.StoreAsync(new MemoryStream(original), "mix");

        // Keep shards {3,4,5,6,7,8,9, 11,12,13} — drop data shards 0,1,2 and parity shard 10.
        // That leaves 7 data + 3 parity = 10 = K, forcing the inversion path on a data+parity mix.
        var keep = new HashSet<int> { 3, 4, 5, 6, 7, 8, 9, 11, 12, 13 };
        Assert.Equal(manifest.K, keep.Count);
        var drop = Enumerable.Range(0, manifest.TotalShards)
            .Where(i => !keep.Contains(i))
            .Select(i => manifest.ShardHashes[i]);
        svc.RemoveShards(drop);

        var recovered = await svc.RecoverAsync(manifest);
        using var ms = new MemoryStream();
        await recovered.CopyToAsync(ms);

        Assert.Equal(original, ms.ToArray());
    }

    // ── 17. Losing M+1=5 shards fails cleanly (below K threshold) ────────────

    [Fact]
    public async Task RecoverAsync_AfterDroppingMPlusOne_ThrowsCleanly()
    {
        var svc = new InMemoryVaultService();
        var original = new byte[1500];
        Random.Shared.NextBytes(original);

        var manifest = await svc.StoreAsync(new MemoryStream(original), "unrecoverable");

        // Drop M+1=5 shards → only K-1=9 remain (below the K-of-N threshold).
        svc.RemoveShards(manifest.ShardHashes.Take(manifest.M + 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecoverAsync(manifest));
    }

    // ── 18. Round-trip SHA-256 of recovered bytes matches the original ───────

    [Fact]
    public async Task RecoverAsync_RoundTripSha256_MatchesOriginal()
    {
        var svc = new InMemoryVaultService();
        var original = new byte[7777];
        Random.Shared.NextBytes(original);
        var originalSha = System.Security.Cryptography.SHA256.HashData(original);

        var manifest = await svc.StoreAsync(new MemoryStream(original), "sha");

        // Drop a data+parity spread (3 shards), still well above K, then recover and hash.
        svc.RemoveShards(new[] { manifest.ShardHashes[0], manifest.ShardHashes[5], manifest.ShardHashes[12] });

        var recovered = await svc.RecoverAsync(manifest);
        using var ms = new MemoryStream();
        await recovered.CopyToAsync(ms);
        var recoveredSha = System.Security.Cryptography.SHA256.HashData(ms.ToArray());

        Assert.Equal(originalSha, recoveredSha);
    }
}

/// <summary>
/// Direct unit tests for <see cref="ReedSolomonCodec"/> — the MDS property: any K of the N shards (in
/// any data/parity combination) reconstruct the K data shards exactly; K-1 or fewer is unrecoverable.
/// </summary>
public sealed class ReedSolomonCodecTests
{
    [Fact]
    public void Encode_ProducesNShards_SystematicDataPrefix()
    {
        const int k = 10, m = 4, shardSize = 16;
        var codec = new ReedSolomonCodec(k, m);

        var dataShards = new byte[k][];
        for (int i = 0; i < k; i++) { dataShards[i] = new byte[shardSize]; Random.Shared.NextBytes(dataShards[i]); }

        var all = codec.Encode(dataShards);

        Assert.Equal(k + m, all.Length);
        Assert.Equal(k + m, codec.ShardCount);
        // Systematic: the first K shards are the data shards verbatim.
        for (int i = 0; i < k; i++)
            Assert.Equal(dataShards[i], all[i]);
    }

    [Fact]
    public void DecodeDataShards_FromAnyKShards_IncludingParityMix_Reconstructs()
    {
        const int k = 10, m = 4, shardSize = 16;
        var codec = new ReedSolomonCodec(k, m);

        var dataShards = new byte[k][];
        for (int i = 0; i < k; i++) { dataShards[i] = new byte[shardSize]; Random.Shared.NextBytes(dataShards[i]); }

        var all = codec.Encode(dataShards);

        // Recover using shards {4..13}: drop data shards 0..3, keep 6 data + all 4 parity = K. Inversion path.
        var available = new Dictionary<int, byte[]>();
        for (int idx = 4; idx < k + m; idx++) available[idx] = all[idx];
        Assert.Equal(k, available.Count);

        var recovered = codec.DecodeDataShards(available);
        for (int i = 0; i < k; i++)
            Assert.Equal(dataShards[i], recovered[i]);
    }

    [Fact]
    public void DecodeDataShards_AllDataShardsPresent_FastPath_Reconstructs()
    {
        const int k = 10, m = 4, shardSize = 8;
        var codec = new ReedSolomonCodec(k, m);

        var dataShards = new byte[k][];
        for (int i = 0; i < k; i++) { dataShards[i] = new byte[shardSize]; Random.Shared.NextBytes(dataShards[i]); }

        var all = codec.Encode(dataShards);
        var available = new Dictionary<int, byte[]>();
        for (int idx = 0; idx < k; idx++) available[idx] = all[idx];

        var recovered = codec.DecodeDataShards(available);
        for (int i = 0; i < k; i++)
            Assert.Equal(dataShards[i], recovered[i]);
    }

    [Fact]
    public void DecodeDataShards_WithKMinusOneShards_Throws()
    {
        const int k = 10, m = 4, shardSize = 8;
        var codec = new ReedSolomonCodec(k, m);

        var dataShards = new byte[k][];
        for (int i = 0; i < k; i++) { dataShards[i] = new byte[shardSize]; Random.Shared.NextBytes(dataShards[i]); }
        var all = codec.Encode(dataShards);

        var available = new Dictionary<int, byte[]>();
        for (int idx = 0; idx < k - 1; idx++) available[idx] = all[idx];

        Assert.Throws<InvalidOperationException>(() => codec.DecodeDataShards(available));
    }
}
