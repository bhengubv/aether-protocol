// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Content.Models;
using AetherNet.Content.Sqlite;
using Xunit;

namespace AetherNet.Content.Sqlite.Tests;

/// <summary>
/// The store's job is that content outlives the process. The load-bearing test is
/// <see cref="Content_SurvivesReopen"/>: reopen the same file and the card is still there, byte for
/// byte — that is what makes "this device hosts that card" true after a reboot.
/// </summary>
public sealed class SqliteContentStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aether-content-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Descriptor_RoundTrips()
    {
        using var store = new SqliteContentStore(_path);
        var bytes = Encoding.UTF8.GetBytes("<h1>Kagiso Corner Store</h1>");
        var descriptor = ContentDescriptor.FromBytes("home", bytes, "application/json");

        await store.SaveDescriptorAsync(descriptor);
        var loaded = await store.GetDescriptorAsync(descriptor.RootHash);

        Assert.NotNull(loaded);
        Assert.Equal(descriptor.RootHash, loaded!.RootHash);
        Assert.Equal("home", loaded.Name);
        Assert.Equal("application/json", loaded.ContentType);
        Assert.Equal(descriptor.ChunkCount, loaded.ChunkCount);
        Assert.Equal(descriptor.ChunkHashes, loaded.ChunkHashes);
        // The manifest must still verify against itself after the round-trip, or chunks can't be checked.
        Assert.True(loaded.VerifySelf());
    }

    [Fact]
    public async Task Chunks_RoundTripAndListInOrder()
    {
        using var store = new SqliteContentStore(_path);
        await store.SaveChunkAsync("ROOT", 2, new byte[] { 3 });
        await store.SaveChunkAsync("ROOT", 0, new byte[] { 1 });
        await store.SaveChunkAsync("ROOT", 1, new byte[] { 2 });

        Assert.Equal(new[] { 0, 1, 2 }, await store.ListChunksAsync("ROOT"));
        Assert.Equal(new byte[] { 2 }, await store.GetChunkAsync("ROOT", 1));
        Assert.Null(await store.GetChunkAsync("ROOT", 9));
    }

    [Fact]
    public async Task RootHash_IsCaseInsensitive_LikeTheInMemoryStore()
    {
        using var store = new SqliteContentStore(_path);
        await store.SaveChunkAsync("abcdef", 0, new byte[] { 7 });
        Assert.Equal(new byte[] { 7 }, await store.GetChunkAsync("ABCDEF", 0));
    }

    [Fact]
    public async Task SaveDescriptor_IsIdempotent()
    {
        using var store = new SqliteContentStore(_path);
        var descriptor = ContentDescriptor.FromBytes("home", Encoding.UTF8.GetBytes("x"), "application/json");

        await store.SaveDescriptorAsync(descriptor);
        await store.SaveDescriptorAsync(descriptor);   // republish on every app start must not duplicate

        Assert.Single(await store.ListDescriptorsAsync());
    }

    [Fact]
    public async Task Content_SurvivesReopen()
    {
        var payload = Encoding.UTF8.GetBytes("""{"blocks":[{"type":"heading","text":"Saturday Market"}]}""");
        var descriptor = ContentDescriptor.FromBytes("market", payload, "application/json");

        using (var first = new SqliteContentStore(_path))
        {
            await first.SaveDescriptorAsync(descriptor);
            await first.SaveChunkAsync(descriptor.RootHash, 0, payload);
        }

        // A different process would see exactly this: the same file, opened cold.
        using var second = new SqliteContentStore(_path);
        var loaded = await second.GetDescriptorAsync(descriptor.RootHash);
        var chunk = await second.GetChunkAsync(descriptor.RootHash, 0);

        Assert.NotNull(loaded);
        Assert.Equal("market", loaded!.Name);
        Assert.Equal(payload, chunk);
        Assert.True(loaded.VerifyChunk(0, chunk!));
    }

    [Fact]
    public async Task Remove_DropsDescriptorAndChunks()
    {
        using var store = new SqliteContentStore(_path);
        var descriptor = ContentDescriptor.FromBytes("home", Encoding.UTF8.GetBytes("x"), "application/json");
        await store.SaveDescriptorAsync(descriptor);
        await store.SaveChunkAsync(descriptor.RootHash, 0, new byte[] { 1 });

        Assert.True(await store.RemoveAsync(descriptor.RootHash));
        Assert.Null(await store.GetDescriptorAsync(descriptor.RootHash));
        Assert.Empty(await store.ListChunksAsync(descriptor.RootHash));
    }

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file)) try { File.Delete(file); } catch (IOException) { }
    }
}
