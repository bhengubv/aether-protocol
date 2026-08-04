// SPDX-License-Identifier: MIT
using AetherNet.Map.Crdt;
using AetherNet.Map.Models;

namespace AetherNet.Map;

/// <summary>
/// In-memory <see cref="IMapStore"/> for tests and single-node scenarios — state is lost on restart.
/// Proximity uses geohash prefix matching over the cell + 8 neighbours; a durable store
/// (<c>AetherNet.Map.Sqlite</c>) backs the same interface with an indexed range scan.
/// </summary>
public sealed class InMemoryMapStore : IMapStore
{
    private readonly Dictionary<string, MapFeatureCrdt> _features = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<MapFeatureCrdt?> GetAsync(string featureId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_features.GetValueOrDefault(featureId));
        }
    }

    public Task ApplyAsync(MapFeatureCrdt incoming, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (_gate)
        {
            if (_features.TryGetValue(incoming.FeatureId, out var existing))
                existing.Merge(incoming);
            else
                _features[incoming.FeatureId] = incoming;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MapFeatureCrdt>> QueryProximityAsync(
        string centerGeohash,
        int radiusCells = 1,
        MapFeatureType? type = null,
        bool includeDeleted = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(centerGeohash);
        var cells = Geohash.CellAndNeighbours(centerGeohash);
        lock (_gate)
        {
            var results = _features.Values
                .Where(f =>
                    (includeDeleted || !f.IsDeleted)
                    && (type is null || f.FeatureType == type.Value)
                    && cells.Any(cell => f.Location.Geohash.StartsWith(cell, StringComparison.Ordinal)))
                .ToList();
            return Task.FromResult<IReadOnlyList<MapFeatureCrdt>>(results);
        }
    }

    public Task<IReadOnlyList<MapFeatureCrdt>> ChangedSinceAsync(HybridLogicalClock cursor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var results = _features.Values.Where(f => f.MaxClock > cursor).ToList();
            return Task.FromResult<IReadOnlyList<MapFeatureCrdt>>(results);
        }
    }

    public Task<HybridLogicalClock> MaxClockAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var max = HybridLogicalClock.Zero;
            foreach (var f in _features.Values)
            {
                var c = f.MaxClock;
                if (c > max) max = c;
            }
            return Task.FromResult(max);
        }
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_features.Count);
        }
    }
}
