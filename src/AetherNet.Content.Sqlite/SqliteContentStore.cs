// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Content.Models;
using Microsoft.Data.Sqlite;

namespace AetherNet.Content.Sqlite;

/// <summary>
/// Durable on-device <see cref="IContentStore"/> backed by SQLite (Microsoft.Data.Sqlite). Content
/// published or pulled by this node — cards, media, any content-addressed blob — survives a restart
/// instead of dying with the process, which is what makes "this device hosts that card" and "a card I
/// collected is mine to keep offline" true rather than a claim.
///
/// One row per descriptor (indexed scalars for querying plus the manifest JSON as the authority) and
/// one row per chunk. Root hashes are compared <c>NOCASE</c> to match
/// <see cref="InMemoryContentStore"/>'s <see cref="StringComparer.OrdinalIgnoreCase"/> semantics.
///
/// A single long-lived connection is serialized by a lock and configured WAL + busy-timeout for
/// reliable single-writer on-device use — same shape as <c>AetherNet.Map.Sqlite</c>.
/// </summary>
public sealed class SqliteContentStore : IContentStore, IDisposable
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public SqliteContentStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // The store owns one long-lived connection, so pooling buys nothing and would keep the file
            // handle open after Dispose. Disable it so the file is fully released on shutdown.
            Pooling = false,
        }.ToString());
        _conn.Open();
        Configure();
        EnsureSchema();
    }

    /// <summary>A private in-memory database (alive for the connection's lifetime) — for tests.</summary>
    public static SqliteContentStore InMemory() => new(":memory:");

    private void Configure()
    {
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA busy_timeout=5000;");
        Exec("PRAGMA synchronous=NORMAL;");
    }

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS content_descriptors (
                root_hash        TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
                name             TEXT NOT NULL,
                total_bytes      INTEGER NOT NULL,
                chunk_size_bytes INTEGER NOT NULL,
                chunk_count      INTEGER NOT NULL,
                content_type     TEXT NOT NULL,
                created_ms       INTEGER NOT NULL,
                manifest         BLOB NOT NULL
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS content_chunks (
                root_hash   TEXT NOT NULL COLLATE NOCASE,
                chunk_index INTEGER NOT NULL,
                bytes       BLOB NOT NULL,
                PRIMARY KEY (root_hash, chunk_index)
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS ix_descriptors_content_type ON content_descriptors(content_type);");
        Exec("CREATE INDEX IF NOT EXISTS ix_descriptors_created_ms ON content_descriptors(created_ms);");
    }

    public Task SaveDescriptorAsync(ContentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrEmpty(descriptor.RootHash);

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO content_descriptors
                    (root_hash, name, total_bytes, chunk_size_bytes, chunk_count, content_type, created_ms, manifest)
                VALUES (@root, @name, @total, @size, @count, @type, @created, @manifest)
                ON CONFLICT(root_hash) DO UPDATE SET
                    name=@name, total_bytes=@total, chunk_size_bytes=@size, chunk_count=@count,
                    content_type=@type, created_ms=@created, manifest=@manifest;
                """;
            cmd.Parameters.AddWithValue("@root", descriptor.RootHash);
            cmd.Parameters.AddWithValue("@name", descriptor.Name);
            cmd.Parameters.AddWithValue("@total", descriptor.TotalBytes);
            cmd.Parameters.AddWithValue("@size", descriptor.ChunkSizeBytes);
            cmd.Parameters.AddWithValue("@count", descriptor.ChunkCount);
            cmd.Parameters.AddWithValue("@type", descriptor.ContentType);
            cmd.Parameters.AddWithValue("@created", new DateTimeOffset(
                DateTime.SpecifyKind(descriptor.CreatedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@manifest", JsonSerializer.SerializeToUtf8Bytes(descriptor, ManifestJson));
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<ContentDescriptor?> GetDescriptorAsync(string rootHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rootHash)) return Task.FromResult<ContentDescriptor?>(null);

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT manifest FROM content_descriptors WHERE root_hash = @root;";
            cmd.Parameters.AddWithValue("@root", rootHash);
            using var reader = cmd.ExecuteReader();
            return Task.FromResult(reader.Read() ? Deserialize((byte[])reader[0]) : null);
        }
    }

    public Task SaveChunkAsync(string rootHash, int chunkIndex, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootHash);
        ArgumentNullException.ThrowIfNull(bytes);

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO content_chunks (root_hash, chunk_index, bytes)
                VALUES (@root, @index, @bytes)
                ON CONFLICT(root_hash, chunk_index) DO UPDATE SET bytes=@bytes;
                """;
            cmd.Parameters.AddWithValue("@root", rootHash);
            cmd.Parameters.AddWithValue("@index", chunkIndex);
            cmd.Parameters.AddWithValue("@bytes", bytes);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetChunkAsync(string rootHash, int chunkIndex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rootHash)) return Task.FromResult<byte[]?>(null);

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT bytes FROM content_chunks WHERE root_hash = @root AND chunk_index = @index;";
            cmd.Parameters.AddWithValue("@root", rootHash);
            cmd.Parameters.AddWithValue("@index", chunkIndex);
            using var reader = cmd.ExecuteReader();
            return Task.FromResult(reader.Read() ? (byte[])reader[0] : null);
        }
    }

    public Task<IReadOnlyList<int>> ListChunksAsync(string rootHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rootHash))
            return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT chunk_index FROM content_chunks WHERE root_hash = @root ORDER BY chunk_index;";
            cmd.Parameters.AddWithValue("@root", rootHash);
            var indices = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                indices.Add(reader.GetInt32(0));
            return Task.FromResult<IReadOnlyList<int>>(indices);
        }
    }

    public Task<IReadOnlyList<ContentDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT manifest FROM content_descriptors;";
            var all = new List<ContentDescriptor>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var descriptor = Deserialize((byte[])reader[0]);
                if (descriptor is not null) all.Add(descriptor);
            }
            return Task.FromResult<IReadOnlyList<ContentDescriptor>>(all);
        }
    }

    /// <summary>
    /// Drop a content and its chunks. Not part of <see cref="IContentStore"/> — the eviction hook a
    /// device needs once it is carrying other people's cards and storage is finite.
    /// </summary>
    public Task<bool> RemoveAsync(string rootHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootHash);

        lock (_gate)
        {
            using var chunks = _conn.CreateCommand();
            chunks.CommandText = "DELETE FROM content_chunks WHERE root_hash = @root;";
            chunks.Parameters.AddWithValue("@root", rootHash);
            chunks.ExecuteNonQuery();

            using var descriptor = _conn.CreateCommand();
            descriptor.CommandText = "DELETE FROM content_descriptors WHERE root_hash = @root;";
            descriptor.Parameters.AddWithValue("@root", rootHash);
            return Task.FromResult(descriptor.ExecuteNonQuery() > 0);
        }
    }

    private static ContentDescriptor? Deserialize(byte[] manifest)
    {
        try { return JsonSerializer.Deserialize<ContentDescriptor>(manifest, ManifestJson); }
        catch (JsonException) { return null; }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
