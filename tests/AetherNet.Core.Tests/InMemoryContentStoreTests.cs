// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Content.Models;
using Xunit;

namespace AetherNet.Core.Tests;

public class InMemoryContentStoreTests
{
    private static ContentDescriptor NewDescriptor(string rootHash = "deadbeef", string name = "blob.bin")
    {
        return new ContentDescriptor
        {
            RootHash = rootHash,
            Name = name,
            TotalBytes = 4,
            ChunkSizeBytes = 2,
            ChunkCount = 2,
            ChunkHashes = new[] { "aa", "bb" },
            ContentType = "application/octet-stream",
        };
    }

    [Fact]
    public async Task SaveDescriptor_ThenGet_RoundTripsByRootHash()
    {
        var store = new InMemoryContentStore();
        var descriptor = NewDescriptor("abc123");

        await store.SaveDescriptorAsync(descriptor);
        var loaded = await store.GetDescriptorAsync("abc123");

        Assert.NotNull(loaded);
        Assert.Equal("abc123", loaded!.RootHash);
        Assert.Equal("blob.bin", loaded.Name);
        Assert.Equal(2, loaded.ChunkCount);
    }

    [Fact]
    public async Task GetDescriptor_UnknownRootHash_ReturnsNull()
    {
        var store = new InMemoryContentStore();
        var loaded = await store.GetDescriptorAsync("never-stored");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task SaveAndGetChunk_RoundTripsBytes_AndListChunksReturnsSortedIndices()
    {
        var store = new InMemoryContentStore();
        var descriptor = NewDescriptor("root1");
        await store.SaveDescriptorAsync(descriptor);

        // Save chunks out of order to verify ListChunks sorts.
        await store.SaveChunkAsync("root1", 1, new byte[] { 9, 9 });
        await store.SaveChunkAsync("root1", 0, new byte[] { 1, 1 });

        var chunk0 = await store.GetChunkAsync("root1", 0);
        var chunk1 = await store.GetChunkAsync("root1", 1);
        var indices = await store.ListChunksAsync("root1");

        Assert.NotNull(chunk0);
        Assert.NotNull(chunk1);
        Assert.Equal(new byte[] { 1, 1 }, chunk0);
        Assert.Equal(new byte[] { 9, 9 }, chunk1);
        Assert.Equal(new[] { 0, 1 }, indices);
    }

    [Fact]
    public async Task GetChunk_MissingChunk_ReturnsNull()
    {
        var store = new InMemoryContentStore();
        var bytes = await store.GetChunkAsync("unknown-root", 0);
        Assert.Null(bytes);
    }

    [Fact]
    public async Task ListChunks_EmptyForUnknownRoot_ReturnsEmpty()
    {
        var store = new InMemoryContentStore();
        var indices = await store.ListChunksAsync("nonexistent");
        Assert.Empty(indices);
    }

    [Fact]
    public async Task SaveDescriptor_Twice_ReplacesExisting()
    {
        var store = new InMemoryContentStore();
        var first = NewDescriptor("samehash", "v1.bin");
        var second = NewDescriptor("samehash", "v2.bin");

        await store.SaveDescriptorAsync(first);
        await store.SaveDescriptorAsync(second);

        var loaded = await store.GetDescriptorAsync("samehash");
        Assert.NotNull(loaded);
        Assert.Equal("v2.bin", loaded!.Name);
    }

    [Fact]
    public async Task ListDescriptors_ReturnsAllSaved()
    {
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(NewDescriptor("hash-a", "a.bin"));
        await store.SaveDescriptorAsync(NewDescriptor("hash-b", "b.bin"));

        var all = await store.ListDescriptorsAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, d => d.RootHash == "hash-a");
        Assert.Contains(all, d => d.RootHash == "hash-b");
    }
}
