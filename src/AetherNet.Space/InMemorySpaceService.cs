// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using AetherNet.Space.Models;

namespace AetherNet.Space;

/// <summary>
/// In-memory implementation of <see cref="ISpaceService"/> for testing and
/// single-node scenarios. All state is lost on restart.
///
/// Proximity matching uses a prefix heuristic: two geohashes are considered
/// "adjacent" if they share the same 4-character prefix (covers roughly the
/// same ~40 km² neighbourhood). This is sufficient for unit tests; production
/// implementations should use a proper geohash-neighbour algorithm.
/// </summary>
public sealed class InMemorySpaceService : ISpaceService
{
    private readonly ConcurrentDictionary<string, SpaceBreadcrumb> _store = new();

    // Key = ContentHash (globally unique per breadcrumb).

    /// <inheritdoc/>
    public event EventHandler<SpaceBreadcrumb>? BreadcrumbReceived;
    /// <inheritdoc/>
    public event EventHandler<SpaceBreadcrumb>? BreadcrumbExpired;

    event EventHandler<SpaceBreadcrumb> ISpaceService.BreadcrumbReceived
    {
        add    => BreadcrumbReceived += value;
        remove => BreadcrumbReceived -= value;
    }

    event EventHandler<SpaceBreadcrumb> ISpaceService.BreadcrumbExpired
    {
        add    => BreadcrumbExpired += value;
        remove => BreadcrumbExpired -= value;
    }

    /// <inheritdoc/>
    public Task<SpaceBreadcrumb> DropAsync(
        string geoHash,
        string contentHash,
        string anchorUhid,
        BreadcrumbType type = BreadcrumbType.Notice,
        int ttlHours = 72,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var effectiveTtl = type == BreadcrumbType.Emergency
            ? 720
            : Math.Clamp(ttlHours, 1, 168);

        var crumb = new SpaceBreadcrumb
        {
            ContentHash  = contentHash,
            GeoHash      = geoHash,
            AnchorUhid   = anchorUhid,
            CreatedAtUtc = DateTime.UtcNow,
            TtlHours     = effectiveTtl,
            Type         = type,
        };

        _store[contentHash] = crumb;
        BreadcrumbReceived?.Invoke(this, crumb);

        return Task.FromResult(crumb);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SpaceBreadcrumb>> ScanAsync(
        string centerGeoHash,
        int radiusCells = 1,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Prefix-based proximity: match the first (6 - radiusCells) chars.
        // radiusCells=1 → prefix length 5 (approx. 1-cell radius)
        // radiusCells=3 → prefix length 3 (wider area)
        var prefixLen = Math.Clamp(6 - radiusCells, 1, 6);
        var prefix    = centerGeoHash.Length >= prefixLen
            ? centerGeoHash[..prefixLen]
            : centerGeoHash;

        var results = _store.Values
            .Where(c => !c.IsExpired && c.GeoHash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        IReadOnlyList<SpaceBreadcrumb> list = results;
        return Task.FromResult(list);
    }

    /// <inheritdoc/>
    public Task PinAsync(SpaceBreadcrumb breadcrumb, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _store[breadcrumb.ContentHash] = breadcrumb;
        BreadcrumbReceived?.Invoke(this, breadcrumb);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(SpaceBreadcrumb breadcrumb, string requestorUhid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_store.TryGetValue(breadcrumb.ContentHash, out var stored))
            return Task.FromResult(false);

        if (!string.Equals(stored.AnchorUhid, requestorUhid, StringComparison.Ordinal))
            return Task.FromResult(false);   // creator-only delete

        var removed = _store.TryRemove(breadcrumb.ContentHash, out _);
        return Task.FromResult(removed);
    }

    /// <inheritdoc/>
    public int PruneExpired()
    {
        var expired = _store.Values.Where(c => c.IsExpired).ToList();
        var count   = 0;
        foreach (var crumb in expired)
        {
            if (_store.TryRemove(crumb.ContentHash, out _))
            {
                BreadcrumbExpired?.Invoke(this, crumb);
                count++;
            }
        }
        return count;
    }
}
