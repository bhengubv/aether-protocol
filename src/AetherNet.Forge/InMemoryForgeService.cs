// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using AetherNet.Forge.Models;

namespace AetherNet.Forge;

/// <summary>
/// In-memory implementation of <see cref="IForgeService"/> for testing and
/// single-node scenarios. All state is lost on restart.
///
/// Thread safety: all mutations use <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// for store-level concurrency and object-level locking for the
/// <see cref="ForgeEntry.DownloadCount"/> read-modify-write.
/// </summary>
public sealed class InMemoryForgeService : IForgeService
{
    // Key = PackageId (e.g. "npm:react@18.2.0")
    private readonly ConcurrentDictionary<string, ForgeEntry> _store = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public event EventHandler<ForgeEntry>? NewEntryAnnounced;

    event EventHandler<ForgeEntry> IForgeService.NewEntryAnnounced
    {
        add    => NewEntryAnnounced += value;
        remove => NewEntryAnnounced -= value;
    }

    /// <inheritdoc/>
    public Task<ForgeEntry?> QueryAsync(string packageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _store.TryGetValue(packageId, out var entry);
        return Task.FromResult(entry);
    }

    /// <inheritdoc/>
    public Task<ForgeEntry> CacheAsync(
        string packageId,
        string contentHash,
        long sizeBytes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // GetOrAdd is atomic — if two threads race to add the same packageId,
        // only the first-written entry survives (idempotent / first-write-wins).
        var isNew = false;
        var entry = _store.GetOrAdd(packageId, _ =>
        {
            isNew = true;
            return new ForgeEntry
            {
                PackageId    = packageId,
                ContentHash  = contentHash,
                SizeBytes    = sizeBytes,
                FetchedAtUtc = DateTime.UtcNow,
                DownloadCount = 0,
            };
        });

        if (isNew)
            NewEntryAnnounced?.Invoke(this, entry);

        return Task.FromResult(entry);
    }

    /// <inheritdoc/>
    public Task<ForgeEntry?> FetchAsync(string packageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_store.TryGetValue(packageId, out var entry))
            return Task.FromResult<ForgeEntry?>(null);

        // Lock on the entry object to atomically increment the auto-property.
        // ConcurrentDictionary already ensures we see the canonical instance;
        // the lock guards the read-modify-write on DownloadCount itself.
        lock (entry)
        {
            entry.DownloadCount++;
        }

        return Task.FromResult<ForgeEntry?>(entry);
    }

    /// <inheritdoc/>
    public Task<ForgeStats> GetStatsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var entries = _store.Values.ToList();

        var totalBytesSaved = entries.Sum(e => (long)e.DownloadCount * e.SizeBytes);
        var topPackages = entries
            .OrderByDescending(e => e.DownloadCount)
            .Take(10)
            .ToList();

        var stats = new ForgeStats
        {
            TotalBytesSaved  = totalBytesSaved,
            TotalPeersServed = 0,   // No peer tracking in the in-memory implementation.
            CatalogueSize    = entries.Count,
            TopPackages      = topPackages,
        };

        return Task.FromResult(stats);
    }
}
