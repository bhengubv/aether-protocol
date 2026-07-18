// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Content.Download;
using AetherNet.Content.Models;
using Xunit;

namespace AetherNet.Content.Download.Tests;

public class MeshIntegrationTests
{
    private const int ChunkSize = 1000;

    private static byte[] MakeFile(int size)
    {
        var f = new byte[size];
        for (int i = 0; i < size; i++) f[i] = (byte)(i * 29 + 3);
        return f;
    }

    [Fact]
    public async Task Downloader_over_MeshChunkSource_pulls_the_whole_file_through_the_content_service()
    {
        var file = MakeFile(20_000);
        var descriptor = ContentDescriptor.FromBytes("mesh.bin", file, "application/octet-stream", ChunkSize);

        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);
        var content = new FakeContentService(store, file, ChunkSize);
        using var source = new MeshChunkSource(content, store);

        var downloader = new SegmentedContentDownloader(store, source,
            new SegmentedDownloadOptions { InitialParallelism = 4, MaxParallelism = 8 });
        using var output = new RecordingStream();

        var result = await downloader.DownloadAsync(descriptor, output);

        Assert.Equal(file, output.ToArray());
        Assert.Equal(descriptor.ChunkCount, result.ChunksFetched);
    }

    [Fact]
    public async Task ContentService_AssembleToAsync_streams_directly_to_offsets_without_a_whole_file_buffer()
    {
        var file = MakeFile(20_000);
        var store = new InMemoryContentStore();
        var service = new ContentService(new NoopMeshSender(), new NoopRoutingService(), store);

        var descriptor = await service.PublishAsync("stream.bin", file, chunkSizeBytes: ChunkSize);

        using var output = new RecordingStream();
        var ok = await service.AssembleToAsync(descriptor.RootHash, output);

        Assert.True(ok);
        Assert.Equal(file, output.ToArray());

        // Streamed chunk-by-chunk to each offset — one write per chunk, each at a chunk boundary.
        Assert.Equal(descriptor.ChunkCount, output.Writes.Count);
        foreach (var (offset, length) in output.Writes)
        {
            Assert.Equal(0, offset % ChunkSize);
            Assert.True(length <= ChunkSize);
        }
    }

    [Fact]
    public async Task ContentService_AssembleToAsync_returns_false_when_a_chunk_is_missing()
    {
        var file = MakeFile(5_000);
        var store = new InMemoryContentStore();
        var service = new ContentService(new NoopMeshSender(), new NoopRoutingService(), store);
        var descriptor = await service.PublishAsync("partial.bin", file, chunkSizeBytes: ChunkSize);

        // Drop one chunk from the store.
        var fresh = new InMemoryContentStore();
        await fresh.SaveDescriptorAsync(descriptor);
        var partialService = new ContentService(new NoopMeshSender(), new NoopRoutingService(), fresh);
        using var output = new RecordingStream();

        Assert.False(await partialService.AssembleToAsync(descriptor.RootHash, output));
    }
}
