// SPDX-License-Identifier: MIT
using AetherMesh.Vault;
using AetherMesh.Vault.Models;
using Xunit;

namespace AetherMesh.Vault.Tests;

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
}
