// SPDX-License-Identifier: MIT
using AetherNet.Map.Crdt;
using AetherNet.Map.Models;
using AetherNet.Map.Wire;
using Microsoft.Data.Sqlite;

namespace AetherNet.Map.Sqlite;

/// <summary>
/// Durable, queryable <see cref="IMapStore"/> backed by SQLite (Microsoft.Data.Sqlite). One row per
/// feature: indexed columns (geohash, type, updated-ms) for querying plus the merge-authoritative
/// <see cref="MapFeatureCodec"/> blob as the source of truth. Proximity queries are a geohash half-open
/// range scan over the cell + 8 neighbours; applying a delta loads-merges-upserts so concurrent edits
/// converge on disk exactly as in memory.
///
/// A single long-lived connection is serialized by a lock and configured WAL + busy-timeout for
/// reliable single-writer on-device use.
/// </summary>
public sealed class SqliteMapStore : IMapStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public SqliteMapStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // The store owns one long-lived connection, so connection pooling buys nothing and would keep
            // the file handle open after Dispose. Disable it so the file is fully released on shutdown.
            Pooling = false,
        }.ToString());
        _conn.Open();
        Configure();
        EnsureSchema();
    }

    /// <summary>A private in-memory store (kept alive for the connection's lifetime) — for tests.</summary>
    public static SqliteMapStore InMemory() => new(":memory:");

    private void Configure()
    {
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA busy_timeout=5000;");
        Exec("PRAGMA synchronous=NORMAL;");
    }

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS map_features (
                feature_id     TEXT PRIMARY KEY NOT NULL,
                feature_type   INTEGER NOT NULL,
                geohash        TEXT NOT NULL,
                lat            REAL NOT NULL,
                lon            REAL NOT NULL,
                authority_mode INTEGER NOT NULL,
                owner_pubkey   BLOB,
                crdt_state     BLOB NOT NULL,
                updated_ms     INTEGER NOT NULL,
                tombstone      INTEGER NOT NULL DEFAULT 0
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS ix_features_geohash ON map_features(geohash);");
        Exec("CREATE INDEX IF NOT EXISTS ix_features_type_geohash ON map_features(feature_type, geohash);");
        Exec("CREATE INDEX IF NOT EXISTS ix_features_updated_ms ON map_features(updated_ms);");
    }

    public Task<MapFeatureCrdt?> GetAsync(string featureId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(LoadById(featureId));
        }
    }

    public Task ApplyAsync(MapFeatureCrdt incoming, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (_gate)
        {
            var existing = LoadById(incoming.FeatureId);
            MapFeatureCrdt merged;
            if (existing is not null)
            {
                existing.Merge(incoming);
                merged = existing;
            }
            else
            {
                merged = incoming;
            }
            Upsert(merged);
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
            using var cmd = _conn.CreateCommand();
            var ranges = new List<string>();
            int i = 0;
            foreach (var cell in cells)
            {
                var end = Geohash.RangeEnd(cell);
                if (end is null)
                {
                    ranges.Add($"geohash >= @lo{i}");
                    cmd.Parameters.AddWithValue($"@lo{i}", cell);
                }
                else
                {
                    ranges.Add($"(geohash >= @lo{i} AND geohash < @hi{i})");
                    cmd.Parameters.AddWithValue($"@lo{i}", cell);
                    cmd.Parameters.AddWithValue($"@hi{i}", end);
                }
                i++;
            }

            var where = "(" + string.Join(" OR ", ranges) + ")";
            if (!includeDeleted) where += " AND tombstone = 0";
            if (type is not null)
            {
                where += " AND feature_type = @type";
                cmd.Parameters.AddWithValue("@type", (int)type.Value);
            }

            cmd.CommandText = $"SELECT crdt_state FROM map_features WHERE {where};";
            return Task.FromResult<IReadOnlyList<MapFeatureCrdt>>(ReadFeatures(cmd));
        }
    }

    public Task<IReadOnlyList<MapFeatureCrdt>> ChangedSinceAsync(HybridLogicalClock cursor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            // Coarse ms filter on the index, then exact HLC filter in memory (idempotent merge tolerates
            // the small same-ms over-fetch).
            cmd.CommandText = "SELECT crdt_state FROM map_features WHERE updated_ms >= @ms;";
            cmd.Parameters.AddWithValue("@ms", cursor.PhysicalMs);
            var filtered = ReadFeatures(cmd).Where(f => f.MaxClock > cursor).ToList();
            return Task.FromResult<IReadOnlyList<MapFeatureCrdt>>(filtered);
        }
    }

    public Task<HybridLogicalClock> MaxClockAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT crdt_state FROM map_features WHERE updated_ms = (SELECT MAX(updated_ms) FROM map_features);";
            var max = HybridLogicalClock.Zero;
            foreach (var f in ReadFeatures(cmd))
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
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM map_features;";
            return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    private MapFeatureCrdt? LoadById(string featureId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT crdt_state FROM map_features WHERE feature_id = @id;";
        cmd.Parameters.AddWithValue("@id", featureId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapFeatureCodec.Deserialize((byte[])reader[0]) : null;
    }

    private void Upsert(MapFeatureCrdt f)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO map_features
                (feature_id, feature_type, geohash, lat, lon, authority_mode, owner_pubkey, crdt_state, updated_ms, tombstone)
            VALUES (@id, @type, @geo, @lat, @lon, @auth, @owner, @state, @ms, @tomb)
            ON CONFLICT(feature_id) DO UPDATE SET
                feature_type=@type, geohash=@geo, lat=@lat, lon=@lon, authority_mode=@auth,
                owner_pubkey=@owner, crdt_state=@state, updated_ms=@ms, tombstone=@tomb;
            """;
        var loc = f.Location;
        cmd.Parameters.AddWithValue("@id", f.FeatureId);
        cmd.Parameters.AddWithValue("@type", (int)f.FeatureType);
        cmd.Parameters.AddWithValue("@geo", loc.Geohash);
        cmd.Parameters.AddWithValue("@lat", loc.Latitude);
        cmd.Parameters.AddWithValue("@lon", loc.Longitude);
        cmd.Parameters.AddWithValue("@auth", (int)f.AuthorityMode);
        cmd.Parameters.AddWithValue("@owner", (object?)f.OwnerPubKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@state", MapFeatureCodec.Serialize(f));
        cmd.Parameters.AddWithValue("@ms", f.MaxClock.PhysicalMs);
        cmd.Parameters.AddWithValue("@tomb", f.IsDeleted ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static List<MapFeatureCrdt> ReadFeatures(SqliteCommand cmd)
    {
        var list = new List<MapFeatureCrdt>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(MapFeatureCodec.Deserialize((byte[])reader[0]));
        return list;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
