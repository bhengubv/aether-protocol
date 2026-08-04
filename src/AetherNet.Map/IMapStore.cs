// SPDX-License-Identifier: MIT
using AetherNet.Map.Crdt;
using AetherNet.Map.Models;

namespace AetherNet.Map;

/// <summary>
/// The on-device neighbourhood map store. Holds one <see cref="MapFeatureCrdt"/> per feature; applying an
/// incoming replica merges it (CRDT join) rather than overwriting. Proximity queries are backed by a
/// geohash range/prefix index; the changed-since cursor drives anti-entropy sync.
///
/// The default <see cref="InMemoryMapStore"/> is for tests and single-node use; a host registers a durable
/// SQLite-backed store (<c>AetherNet.Map.Sqlite</c>) before <c>AddMap()</c> to persist across restarts.
/// </summary>
public interface IMapStore
{
    /// <summary>Get a feature by id, or null if unknown.</summary>
    Task<MapFeatureCrdt?> GetAsync(string featureId, CancellationToken ct = default);

    /// <summary>Merge an incoming feature replica into the store (insert if new). Convergent and idempotent.</summary>
    Task ApplyAsync(MapFeatureCrdt incoming, CancellationToken ct = default);

    /// <summary>
    /// Features within the geohash cell of <paramref name="centerGeohash"/> plus its 8 neighbours,
    /// optionally filtered by <paramref name="type"/>. Tombstoned features are excluded unless
    /// <paramref name="includeDeleted"/> is set.
    /// </summary>
    Task<IReadOnlyList<MapFeatureCrdt>> QueryProximityAsync(
        string centerGeohash,
        int radiusCells = 1,
        MapFeatureType? type = null,
        bool includeDeleted = false,
        CancellationToken ct = default);

    /// <summary>Features whose max HLC is greater than <paramref name="cursor"/> (the anti-entropy delta set).</summary>
    Task<IReadOnlyList<MapFeatureCrdt>> ChangedSinceAsync(HybridLogicalClock cursor, CancellationToken ct = default);

    /// <summary>The greatest HLC across all stored features — the local sync cursor to advertise/pull against.</summary>
    Task<HybridLogicalClock> MaxClockAsync(CancellationToken ct = default);

    /// <summary>Number of features held (including tombstoned).</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
