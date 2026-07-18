// SPDX-License-Identifier: MIT

using System.Diagnostics;
using AetherNet.Content;
using AetherNet.Content.Download;
using AetherNet.Content.Models;
using Xunit;

namespace AetherNet.Content.Download.Tests;

public class SegmentedContentDownloaderTests
{
    private const int ChunkSize = 1000;

    private static byte[] MakeFile(int size)
    {
        var f = new byte[size];
        for (int i = 0; i < size; i++) f[i] = (byte)(i * 53 + 11);
        return f;
    }

    private static ContentDescriptor Descriptor(byte[] file) =>
        ContentDescriptor.FromBytes("payload.bin", file, "application/octet-stream", ChunkSize);

    [Fact]
    public async Task Downloads_every_chunk_and_writes_the_exact_file()
    {
        var file = MakeFile(20_000); // 20 chunks
        var descriptor = Descriptor(file);
        var source = new ScriptedChunkSource(file, ChunkSize);
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);

        var downloader = new SegmentedContentDownloader(store, source);
        using var output = new RecordingStream();

        var result = await downloader.DownloadAsync(descriptor, output);

        Assert.Equal(file, output.ToArray());
        Assert.Equal(descriptor.ChunkCount, result.ChunksFetched);
        Assert.Equal(0, result.ChunksResumed);
    }

    [Fact]
    public async Task Concurrent_fetching_is_much_faster_than_serial()
    {
        var file = MakeFile(24_000); // 24 chunks
        var descriptor = Descriptor(file);
        var source = new ScriptedChunkSource(file, ChunkSize) { DefaultLatency = TimeSpan.FromMilliseconds(25) };
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);

        var downloader = new SegmentedContentDownloader(store, source,
            new SegmentedDownloadOptions { InitialParallelism = 8, MaxParallelism = 8, EnableAdaptiveAcceleration = false });
        using var output = new RecordingStream();

        var sw = Stopwatch.StartNew();
        await downloader.DownloadAsync(descriptor, output);
        sw.Stop();

        // Serial would be ~24 * 25ms = 600ms. With 8 in flight it should be well under half.
        Assert.True(sw.ElapsedMilliseconds < 300, $"expected concurrency; took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(file, output.ToArray());
    }

    [Fact]
    public async Task Writes_directly_to_each_chunk_offset_never_a_whole_file_buffer()
    {
        var file = MakeFile(20_000);
        var descriptor = Descriptor(file);
        var source = new ScriptedChunkSource(file, ChunkSize);
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);

        var downloader = new SegmentedContentDownloader(store, source);
        using var output = new RecordingStream();

        await downloader.DownloadAsync(descriptor, output);

        // Every write lands at an exact chunk boundary and is at most one chunk long — no merge pass.
        Assert.Equal(descriptor.ChunkCount, output.Writes.Count);
        foreach (var (offset, length) in output.Writes)
        {
            Assert.Equal(0, offset % ChunkSize);
            Assert.True(length <= ChunkSize);
        }
        Assert.Equal(file, output.ToArray());
    }

    [Fact]
    public async Task Resumes_from_store_without_refetching_completed_chunks()
    {
        var file = MakeFile(20_000);
        var descriptor = Descriptor(file);
        var source = new ScriptedChunkSource(file, ChunkSize);
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);

        // Pre-seed the even chunks (as if a prior run had completed them).
        int seeded = 0;
        for (int i = 0; i < descriptor.ChunkCount; i += 2)
        {
            int start = i * ChunkSize;
            int len = Math.Min(ChunkSize, file.Length - start);
            var bytes = new byte[len];
            Array.Copy(file, start, bytes, 0, len);
            await store.SaveChunkAsync(descriptor.RootHash, i, bytes);
            seeded++;
        }

        var downloader = new SegmentedContentDownloader(store, source);
        using var output = new RecordingStream();

        var result = await downloader.DownloadAsync(descriptor, output);

        Assert.Equal(seeded, result.ChunksResumed);
        Assert.Equal(descriptor.ChunkCount - seeded, result.ChunksFetched);
        Assert.Equal(descriptor.ChunkCount - seeded, source.TotalFetches); // completed chunks never re-fetched
        Assert.Equal(file, output.ToArray());
    }

    [Fact]
    public async Task Retries_transient_failures_with_backoff_then_completes()
    {
        var file = MakeFile(8_000); // 8 chunks
        var descriptor = Descriptor(file);
        var source = new ScriptedChunkSource(file, ChunkSize);
        source.FailTransiently(3, 2); // chunk 3 fails twice, then succeeds
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);

        var downloader = new SegmentedContentDownloader(store, source,
            new SegmentedDownloadOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
        using var output = new RecordingStream();

        var result = await downloader.DownloadAsync(descriptor, output);

        Assert.True(result.Retries >= 2, $"expected >= 2 retries, got {result.Retries}");
        Assert.Equal(3, source.FetchesFor(3)); // 2 failures + 1 success
        Assert.Equal(file, output.ToArray());
    }

    [Fact]
    public async Task Permanent_failure_surfaces_as_a_single_ContentDownloadException()
    {
        var file = MakeFile(20_000);
        var descriptor = Descriptor(file);
        var source = new ScriptedChunkSource(file, ChunkSize);
        source.FailPermanently(7);
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);

        var downloader = new SegmentedContentDownloader(store, source);
        using var output = new RecordingStream();

        // Must throw (not hang) and must be the unified boundary exception.
        var ex = await Assert.ThrowsAsync<ContentDownloadException>(
            () => downloader.DownloadAsync(descriptor, output));
        Assert.Contains("7", ex.Message + ex.InnerException?.Message);
    }

    [Fact]
    public async Task Idle_workers_steal_work_when_one_segment_is_slow()
    {
        var file = MakeFile(40_000); // 40 chunks
        var descriptor = Descriptor(file);
        // Chunks 0-9 are slow; the rest are instant. Fast workers should steal from the slow segment.
        var source = new ScriptedChunkSource(file, ChunkSize)
        {
            LatencyFor = i => i < 10 ? TimeSpan.FromMilliseconds(20) : TimeSpan.Zero,
        };
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);

        var downloader = new SegmentedContentDownloader(store, source,
            new SegmentedDownloadOptions { InitialParallelism = 4, MaxParallelism = 8 });
        using var output = new RecordingStream();

        var result = await downloader.DownloadAsync(descriptor, output);

        Assert.True(result.SegmentSteals > 0, "expected work-stealing/re-split to occur");
        Assert.Equal(file, output.ToArray());
    }

    [Fact]
    public async Task Adaptive_controller_stays_within_configured_bounds()
    {
        var file = MakeFile(30_000);
        var descriptor = Descriptor(file);
        var source = new ScriptedChunkSource(file, ChunkSize) { DefaultLatency = TimeSpan.FromMilliseconds(5) };
        var store = new InMemoryContentStore();
        await store.SaveDescriptorAsync(descriptor);

        var opts = new SegmentedDownloadOptions
        {
            InitialParallelism = 2,
            MinParallelism = 1,
            MaxParallelism = 6,
            EnableAdaptiveAcceleration = true,
            AccelerationInterval = TimeSpan.FromMilliseconds(10),
        };
        var downloader = new SegmentedContentDownloader(store, source, opts);
        using var output = new RecordingStream();

        var result = await downloader.DownloadAsync(descriptor, output);

        Assert.InRange(result.MaxObservedParallelism, opts.MinParallelism, opts.MaxParallelism);
        Assert.Equal(file, output.ToArray());
    }
}
