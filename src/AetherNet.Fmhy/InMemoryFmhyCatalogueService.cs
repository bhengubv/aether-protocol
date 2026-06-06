// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net.Http;
using AetherNet.Fmhy.Models;

namespace AetherNet.Fmhy;

/// <summary>
/// In-memory <see cref="IFmhyCatalogueService"/> backed by the parsed FMHY markdown.
/// Suitable for testing and single-node scenarios; production nodes layer on
/// <c>IDtnService</c> propagation above this service.
///
/// <para>
/// Construction accepts an optional seed entry list (e.g., loaded from
/// <c>fixtures/fmhy/seed-catalogue.json</c>).  <see cref="SyncAsync"/> accepts
/// a raw FMHY markdown string or, when an <see cref="HttpClient"/> is supplied,
/// fetches it from <c>api.fmhy.net/single-page</c>.
/// </para>
/// </summary>
public sealed class InMemoryFmhyCatalogueService : IFmhyCatalogueService
{
    private readonly HttpClient?         _http;
    private volatile IReadOnlyList<FmhyEntry> _entries;
    private DateTime?                     _lastSyncedAt;

    /// <summary>Public FMHY single-page endpoint.</summary>
    public const string FmhyApiUrl = "https://api.fmhy.net/single-page";

    // ── Well-known public tracker list aggregators ────────────────────────────
    private static readonly TrackerSource[] BuiltInTrackerSources =
    [
        new("ngosang/trackerslist",
            "https://ngosang.github.io/trackerslist/trackers_all.txt",
            "Community-maintained list of all known public BitTorrent trackers."),

        new("XIU2/TrackersListCollection (all)",
            "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/all.txt",
            "Comprehensive tracker collection maintained by XIU2, updated daily."),

        new("XIU2/TrackersListCollection (best)",
            "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt",
            "Curated best-performing tracker subset from the XIU2 collection."),

        new("newtrackon (stable)",
            "https://newtrackon.com/api/stable",
            "Live-monitored stable tracker list from newtrackon.com."),

        new("openwebtorrent",
            "https://openwebtorrent.com/",
            "Free WebTorrent-compatible tracker for browser-based torrenting."),
    ];

    /// <summary>
    /// Initialise with an optional seed entry list and optional HTTP client.
    /// When <paramref name="httpClient"/> is <c>null</c>, <see cref="SyncAsync"/>
    /// requires the markdown to be passed explicitly.
    /// </summary>
    public InMemoryFmhyCatalogueService(
        IReadOnlyList<FmhyEntry>? seedEntries = null,
        HttpClient?               httpClient  = null)
    {
        _entries = seedEntries ?? Array.Empty<FmhyEntry>();
        _http    = httpClient;
    }

    /// <inheritdoc/>
    public DateTime? LastSyncedAt => _lastSyncedAt;

    /// <inheritdoc/>
    public int EntryCount => _entries.Count;

    /// <inheritdoc/>
    public event EventHandler<FmhySyncEventArgs>? Synced;

    // ── IFmhyCatalogueService explicit (non-nullable event) ──────────────────
    event EventHandler<FmhySyncEventArgs> IFmhyCatalogueService.Synced
    {
        add    => Synced += value;
        remove => Synced -= value;
    }

    /// <inheritdoc/>
    public async Task SyncAsync(string? markdownContent = null, CancellationToken ct = default)
    {
        string markdown;

        if (markdownContent is not null)
        {
            markdown = markdownContent;
        }
        else if (_http is not null)
        {
            var response = await _http.GetStringAsync(FmhyApiUrl, ct).ConfigureAwait(false);
            markdown     = response;
        }
        else
        {
            // No markdown provided and no HTTP client — nothing to sync.
            return;
        }

        var before  = _entries.Count;
        var parsed  = FmhyMarkdownParser.Parse(markdown);
        var now     = DateTime.UtcNow;

        _entries      = parsed;
        _lastSyncedAt = now;

        Synced?.Invoke(this, new FmhySyncEventArgs(
            totalEntries: parsed.Count,
            newEntries:   parsed.Count - before,
            syncedAt:     now));
    }

    /// <inheritdoc/>
    public IReadOnlyList<FmhyEntry> Browse(string? categoryFilter = null)
    {
        var all = _entries;
        if (string.IsNullOrEmpty(categoryFilter)) return all;

        return all.Where(e => e.Category.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase))
                  .ToList()
                  .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<FmhyEntry> GetStarred(string? categoryFilter = null)
    {
        var all = _entries;
        IEnumerable<FmhyEntry> starred = all.Where(e => e.IsStarred);

        if (!string.IsNullOrEmpty(categoryFilter))
            starred = starred.Where(e => e.Category.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase));

        return starred.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<TrackerSource> GetTrackerSources() => BuiltInTrackerSources;
}
