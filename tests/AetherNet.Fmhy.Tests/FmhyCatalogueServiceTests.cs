// SPDX-License-Identifier: MIT

using AetherNet.Fmhy;
using AetherNet.Fmhy.Models;
using Xunit;

namespace AetherNet.Fmhy.Tests;

public sealed class FmhyCatalogueServiceTests
{
    private static readonly FmhyEntry[] SeedEntries =
    [
        new("mpv",           "https://mpv.io/",                 "Open-source player.",       "Video Tools",     IsStarred: true,  Mirrors: []),
        new("VLC",           "https://www.videolan.org/",       "Versatile player.",         "Video Tools",     IsStarred: false, Mirrors: []),
        new("qBittorrent",   "https://www.qbittorrent.org/",    "Free torrent client.",      "Torrent Clients", IsStarred: true,  Mirrors: []),
        new("Internet Archive", "https://archive.org/",         "Digital library.",          "Public Domain",   IsStarred: true,  Mirrors: []),
        new("Invidious",     "https://invidious.io/",           "YouTube alternative.",      "Streaming Sites", IsStarred: true,  Mirrors: []),
    ];

    // ── Browse ────────────────────────────────────────────────────────────────

    [Fact]
    public void Browse_NoFilter_ReturnsAllEntries()
    {
        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        Assert.Equal(SeedEntries.Length, svc.Browse().Count);
    }

    [Fact]
    public void Browse_CategoryFilter_ReturnsCategorySubset()
    {
        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        var video = svc.Browse("Video");
        Assert.Equal(2, video.Count);
        Assert.All(video, e => Assert.Contains("Video", e.Category, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Browse_CaseInsensitiveFilter()
    {
        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        Assert.Equal(svc.Browse("torrent").Count, svc.Browse("TORRENT").Count);
    }

    [Fact]
    public void Browse_NoMatch_ReturnsEmpty()
    {
        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        Assert.Empty(svc.Browse("DoesNotExistCategory99"));
    }

    // ── GetStarred ────────────────────────────────────────────────────────────

    [Fact]
    public void GetStarred_ReturnsOnlyStarredEntries()
    {
        var svc     = new InMemoryFmhyCatalogueService(SeedEntries);
        var starred = svc.GetStarred();
        Assert.Equal(4, starred.Count);
        Assert.All(starred, e => Assert.True(e.IsStarred));
    }

    [Fact]
    public void GetStarred_WithCategoryFilter_Narrows()
    {
        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        var starredVideo = svc.GetStarred("Video");
        // mpv is starred Video; VLC is not starred
        Assert.Single(starredVideo);
        Assert.Equal("mpv", starredVideo[0].Name);
    }

    // ── SyncAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_WithMarkdown_UpdatesEntries()
    {
        const string md = """
            # New Category

            * ⭐ **[NewApp](https://newapp.io/)** - Fresh addition.
            """;

        var svc = new InMemoryFmhyCatalogueService(); // empty
        Assert.Equal(0, svc.EntryCount);

        await svc.SyncAsync(md);

        Assert.Equal(1, svc.EntryCount);
        Assert.NotNull(svc.LastSyncedAt);
    }

    [Fact]
    public async Task SyncAsync_RaisesSyncedEvent()
    {
        const string md = """
            # Tools

            * **[App1](https://app1.io/)** - First.
            * **[App2](https://app2.io/)** - Second.
            """;

        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        FmhySyncEventArgs? eventArgs = null;
        svc.Synced += (_, e) => eventArgs = e;

        await svc.SyncAsync(md);

        Assert.NotNull(eventArgs);
        Assert.Equal(2, eventArgs.TotalEntries);
    }

    [Fact]
    public async Task SyncAsync_NoMarkdownNoHttpClient_DoesNotThrow()
    {
        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        // No HTTP client → SyncAsync(null) is a no-op.
        await svc.SyncAsync(null);
        Assert.Equal(SeedEntries.Length, svc.EntryCount);
        Assert.Null(svc.LastSyncedAt);
    }

    // ── GetTrackerSources ─────────────────────────────────────────────────────

    [Fact]
    public void GetTrackerSources_ReturnsBuiltInList()
    {
        var svc     = new InMemoryFmhyCatalogueService();
        var sources = svc.GetTrackerSources();
        Assert.NotEmpty(sources);
        Assert.All(sources, s =>
        {
            Assert.False(string.IsNullOrEmpty(s.Name));
            Assert.False(string.IsNullOrEmpty(s.Url));
        });
    }

    [Fact]
    public void GetTrackerSources_ContainsNgosang()
    {
        var svc     = new InMemoryFmhyCatalogueService();
        var sources = svc.GetTrackerSources();
        Assert.Contains(sources, s => s.Url.Contains("ngosang", StringComparison.OrdinalIgnoreCase));
    }

    // ── FmhySeedLoader ────────────────────────────────────────────────────────

    [Fact]
    public void SeedLoader_LoadsFromValidJson()
    {
        const string json = """
            [
              { "name": "mpv", "url": "https://mpv.io/", "description": "Player",
                "category": "Video Tools", "isStarred": true, "mirrors": [] }
            ]
            """;
        var entries = FmhySeedLoader.LoadFromJson(json);
        Assert.Single(entries);
        Assert.Equal("mpv", entries[0].Name);
        Assert.True(entries[0].IsStarred);
    }

    [Fact]
    public void SeedLoader_SkipsEntriesWithEmptyUrl()
    {
        const string json = """
            [
              { "name": "Good",    "url": "https://good.io/", "category": "X", "isStarred": false, "mirrors": [] },
              { "name": "Bad",     "url": "",                  "category": "X", "isStarred": false, "mirrors": [] },
              { "name": "Missing",                             "category": "X", "isStarred": false, "mirrors": [] }
            ]
            """;
        var entries = FmhySeedLoader.LoadFromJson(json);
        Assert.Single(entries);
        Assert.Equal("Good", entries[0].Name);
    }

    [Fact]
    public void SeedLoader_EmptyArray_ReturnsEmpty()
    {
        var entries = FmhySeedLoader.LoadFromJson("[]");
        Assert.Empty(entries);
    }

    // ── EntryCount + LastSyncedAt ─────────────────────────────────────────────

    [Fact]
    public void EntryCount_ReflectsLoadedSeed()
    {
        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        Assert.Equal(SeedEntries.Length, svc.EntryCount);
    }

    [Fact]
    public void LastSyncedAt_NullBeforeSync()
    {
        var svc = new InMemoryFmhyCatalogueService(SeedEntries);
        Assert.Null(svc.LastSyncedAt);
    }
}
