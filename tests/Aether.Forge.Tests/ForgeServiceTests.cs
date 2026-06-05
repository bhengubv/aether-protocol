// SPDX-License-Identifier: MIT
using AetherMesh.Forge;
using AetherMesh.Forge.Models;
using Xunit;

namespace AetherMesh.Forge.Tests;

public sealed class ForgeServiceTests
{
    // ── 1. QueryAsync returns null for an unknown package ────────────────────

    [Fact]
    public async Task QueryAsync_ReturnsNullForUnknownPackage()
    {
        var svc = new InMemoryForgeService();

        var result = await svc.QueryAsync("npm:unknown@0.0.0");

        Assert.Null(result);
    }

    // ── 2. CacheAsync stores a new entry ─────────────────────────────────────

    [Fact]
    public async Task CacheAsync_StoresNewEntry()
    {
        var svc = new InMemoryForgeService();

        var entry = await svc.CacheAsync(
            packageId:   "npm:react@18.2.0",
            contentHash: "sha256:abc123",
            sizeBytes:   512_000);

        Assert.NotNull(entry);
        Assert.Equal("npm:react@18.2.0", entry.PackageId);
        Assert.Equal("sha256:abc123",    entry.ContentHash);
        Assert.Equal(512_000,            entry.SizeBytes);
        Assert.Equal(0,                  entry.DownloadCount);

        // Must also be retrievable via QueryAsync
        var queried = await svc.QueryAsync("npm:react@18.2.0");
        Assert.NotNull(queried);
        Assert.Equal("sha256:abc123", queried!.ContentHash);
    }

    // ── 3. CacheAsync is idempotent — returns existing entry ─────────────────

    [Fact]
    public async Task CacheAsync_IsIdempotent_ReturnsExistingEntry()
    {
        var svc = new InMemoryForgeService();

        var first = await svc.CacheAsync("pip:requests@2.31.0", "sha256:first",  100_000);
        var second = await svc.CacheAsync("pip:requests@2.31.0", "sha256:second", 200_000);

        // Both calls must return the same object (first write wins).
        Assert.Same(first, second);
        Assert.Equal("sha256:first", second.ContentHash);
        Assert.Equal(100_000,        second.SizeBytes);
    }

    // ── 4. FetchAsync returns null for an unknown package ────────────────────

    [Fact]
    public async Task FetchAsync_ReturnsNullForUnknownPackage()
    {
        var svc = new InMemoryForgeService();

        var result = await svc.FetchAsync("cargo:nonexistent@1.0.0");

        Assert.Null(result);
    }

    // ── 5. FetchAsync increments download count ───────────────────────────────

    [Fact]
    public async Task FetchAsync_IncrementsDownloadCount()
    {
        var svc = new InMemoryForgeService();
        await svc.CacheAsync("go:golang.org/x/net@v0.21.0", "sha256:gonet", 300_000);

        var result = await svc.FetchAsync("go:golang.org/x/net@v0.21.0");

        Assert.NotNull(result);
        Assert.Equal(1, result!.DownloadCount);
    }

    // ── 6. FetchAsync called twice yields DownloadCount = 2 ──────────────────

    [Fact]
    public async Task FetchAsync_IncrementsTwice_DownloadCountIsTwo()
    {
        var svc = new InMemoryForgeService();
        await svc.CacheAsync("nuget:Newtonsoft.Json@13.0.3", "sha256:newtonsoft", 750_000);

        await svc.FetchAsync("nuget:Newtonsoft.Json@13.0.3");
        var result = await svc.FetchAsync("nuget:Newtonsoft.Json@13.0.3");

        Assert.NotNull(result);
        Assert.Equal(2, result!.DownloadCount);
    }

    // ── 7. GetStatsAsync — CatalogueSize matches entry count ─────────────────

    [Fact]
    public async Task GetStatsAsync_CatalogueSizeMatchesEntryCount()
    {
        var svc = new InMemoryForgeService();
        await svc.CacheAsync("npm:lodash@4.17.21",    "sha256:lodash",    100_000);
        await svc.CacheAsync("npm:axios@1.6.2",       "sha256:axios",     200_000);
        await svc.CacheAsync("pip:django@4.2.0",      "sha256:django",    300_000);

        var stats = await svc.GetStatsAsync();

        Assert.Equal(3, stats.CatalogueSize);
    }

    // ── 8. GetStatsAsync — TotalBytesSaved = sum(DownloadCount * SizeBytes) ──

    [Fact]
    public async Task GetStatsAsync_TotalBytesSaved_IsDownloadCountTimesSizeBytes()
    {
        var svc = new InMemoryForgeService();
        await svc.CacheAsync("cargo:serde@1.0.195",   "sha256:serde",   50_000);
        await svc.CacheAsync("cargo:tokio@1.35.1",    "sha256:tokio",  150_000);

        // Fetch serde 3 times, tokio 2 times
        await svc.FetchAsync("cargo:serde@1.0.195");
        await svc.FetchAsync("cargo:serde@1.0.195");
        await svc.FetchAsync("cargo:serde@1.0.195");
        await svc.FetchAsync("cargo:tokio@1.35.1");
        await svc.FetchAsync("cargo:tokio@1.35.1");

        var stats = await svc.GetStatsAsync();

        // 3 × 50_000 + 2 × 150_000 = 150_000 + 300_000 = 450_000
        Assert.Equal(450_000L, stats.TotalBytesSaved);
    }

    // ── 9. GetStatsAsync — TopPackages sorted by DownloadCount descending ────

    [Fact]
    public async Task GetStatsAsync_TopPackages_SortedByDownloadCountDescending()
    {
        var svc = new InMemoryForgeService();
        await svc.CacheAsync("npm:alpha@1.0.0", "sha256:a", 10_000);
        await svc.CacheAsync("npm:beta@1.0.0",  "sha256:b", 10_000);
        await svc.CacheAsync("npm:gamma@1.0.0", "sha256:c", 10_000);

        // gamma: 5 downloads, alpha: 2, beta: 1
        for (var i = 0; i < 5; i++) await svc.FetchAsync("npm:gamma@1.0.0");
        for (var i = 0; i < 2; i++) await svc.FetchAsync("npm:alpha@1.0.0");
        await svc.FetchAsync("npm:beta@1.0.0");

        var stats = await svc.GetStatsAsync();

        Assert.Equal(3, stats.TopPackages.Count);
        Assert.Equal("npm:gamma@1.0.0", stats.TopPackages[0].PackageId);
        Assert.Equal("npm:alpha@1.0.0", stats.TopPackages[1].PackageId);
        Assert.Equal("npm:beta@1.0.0",  stats.TopPackages[2].PackageId);
    }

    // ── 10. NewEntryAnnounced fires once for new, not for idempotent re-cache ─

    [Fact]
    public async Task NewEntryAnnounced_FiresWhenNewEntryIsCached()
    {
        var svc = new InMemoryForgeService();

        var announcements = new List<ForgeEntry>();
        ((IForgeService)svc).NewEntryAnnounced += (_, e) => announcements.Add(e);

        // First call — should fire the event
        await svc.CacheAsync("git:github.com/org/repo@abc123", "sha256:git1", 5_000_000);

        // Second call with the same packageId — must NOT fire again
        await svc.CacheAsync("git:github.com/org/repo@abc123", "sha256:git2", 9_000_000);

        Assert.Single(announcements);
        Assert.Equal("git:github.com/org/repo@abc123", announcements[0].PackageId);
        Assert.Equal("sha256:git1", announcements[0].ContentHash);
    }
}
