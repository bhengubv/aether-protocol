// SPDX-License-Identifier: MIT

using AetherMesh.Fmhy.Models;

namespace AetherMesh.Fmhy;

// ─────────────────────────────────────────────────────────────────────────────
//  IFmhyCatalogueService — FMHY content discovery over the Aether mesh
//
//  Aether Media bundles a seed snapshot of the FMHY directory at build time
//  (fixtures/fmhy/seed-catalogue.json) so content discovery works offline
//  immediately on first launch.
//
//  When internet becomes available, SyncAsync() fetches the full markdown from
//  api.fmhy.net/single-page, parses it, and stores the result.  Peers that
//  synced recently propagate the updated catalogue to offline peers via
//  IDtnService — no internet ever required by the second user.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Provides access to the Free Media Heck Yeah (FMHY) content catalogue,
/// propagated over the Aether mesh so offline peers benefit from entries
/// fetched by connected peers.
///
/// <para>
/// Register via DI. A bundled seed catalogue is loaded on construction so
/// <see cref="Browse"/> returns useful results before any sync has run.
/// </para>
/// </summary>
public interface IFmhyCatalogueService
{
    /// <summary>UTC timestamp of the last successful sync, or <c>null</c> if running from seed only.</summary>
    DateTime? LastSyncedAt { get; }

    /// <summary>Total number of entries currently loaded (seed + synced).</summary>
    int EntryCount { get; }

    /// <summary>
    /// Synchronise the catalogue from a fresh FMHY markdown string.
    /// Pass <paramref name="markdownContent"/> directly (e.g., received from a mesh peer),
    /// or leave <c>null</c> to trigger an HTTP fetch from <c>api.fmhy.net/single-page</c>
    /// (requires internet access).
    /// </summary>
    Task SyncAsync(string? markdownContent = null, CancellationToken ct = default);

    /// <summary>
    /// Browse all entries, optionally filtered to a specific category substring.
    /// Category matching is case-insensitive substring (e.g., "torrent" matches
    /// "Torrent Sites", "Torrent Clients", "Audio Torrenting", etc.).
    /// </summary>
    IReadOnlyList<FmhyEntry> Browse(string? categoryFilter = null);

    /// <summary>
    /// Returns only ⭐ starred entries — FMHY's "highly recommended" tier.
    /// </summary>
    IReadOnlyList<FmhyEntry> GetStarred(string? categoryFilter = null);

    /// <summary>
    /// Returns the well-known public torrent tracker list aggregator URLs
    /// bundled with this release.  These can be used to seed a
    /// <c>BitTorrent.Tracker.Client</c> announce list for mesh content sharing.
    /// </summary>
    IReadOnlyList<TrackerSource> GetTrackerSources();

    /// <summary>Raised when new entries arrive via a mesh sync or peer delivery.</summary>
    event EventHandler<FmhySyncEventArgs> Synced;
}

/// <summary>A known torrent tracker list aggregator.</summary>
/// <param name="Name">Human-readable name.</param>
/// <param name="Url">URL of the tracker list (one tracker URL per line, plain-text).</param>
/// <param name="Description">Brief description.</param>
public sealed record TrackerSource(string Name, string Url, string Description);

/// <summary>Event arguments for <see cref="IFmhyCatalogueService.Synced"/>.</summary>
public sealed class FmhySyncEventArgs(int totalEntries, int newEntries, DateTime syncedAt) : EventArgs
{
    /// <summary>Total entries in the catalogue after this sync.</summary>
    public int TotalEntries { get; } = totalEntries;

    /// <summary>New entries added by this sync (0 when loaded from seed).</summary>
    public int NewEntries   { get; } = newEntries;

    /// <summary>UTC time of the sync.</summary>
    public DateTime SyncedAt { get; } = syncedAt;
}
