// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Content.Download;

namespace AetherNet.Content.Download.Tests;

/// <summary>
/// A chunk source backed by an in-memory file, with scriptable latency and transient/permanent
/// failures, and a fetch counter — enough to exercise concurrency, retry, resume, and re-split.
/// </summary>
internal sealed class ScriptedChunkSource : IChunkSource
{
    private readonly byte[] _file;
    private readonly int _chunkSize;
    private readonly ConcurrentDictionary<int, int> _transient = new();
    private readonly ConcurrentDictionary<int, byte> _permanent = new();
    private readonly ConcurrentDictionary<int, int> _fetchesByIndex = new();
    private int _totalFetches;

    public ScriptedChunkSource(byte[] file, int chunkSize)
    {
        _file = file;
        _chunkSize = chunkSize;
    }

    public TimeSpan DefaultLatency { get; set; } = TimeSpan.Zero;
    public Func<int, TimeSpan>? LatencyFor { get; set; }
    public int TotalFetches => Volatile.Read(ref _totalFetches);
    public int FetchesFor(int index) => _fetchesByIndex.TryGetValue(index, out var n) ? n : 0;

    public void FailTransiently(int index, int times) => _transient[index] = times;
    public void FailPermanently(int index) => _permanent[index] = 1;

    public async Task<byte[]> FetchChunkAsync(string rootHash, int chunkIndex, string? preferredPeer, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _totalFetches);
        _fetchesByIndex.AddOrUpdate(chunkIndex, 1, (_, n) => n + 1);

        var latency = LatencyFor?.Invoke(chunkIndex) ?? DefaultLatency;
        if (latency > TimeSpan.Zero) await Task.Delay(latency, cancellationToken).ConfigureAwait(false);

        if (_permanent.ContainsKey(chunkIndex))
            throw new ChunkSourceException($"permanent failure on chunk {chunkIndex}", permanent: true);

        if (_transient.TryGetValue(chunkIndex, out var remaining) && remaining > 0)
        {
            _transient[chunkIndex] = remaining - 1;
            throw new ChunkSourceException($"transient failure on chunk {chunkIndex}", permanent: false);
        }

        int start = chunkIndex * _chunkSize;
        int len = Math.Min(_chunkSize, _file.Length - start);
        var bytes = new byte[len];
        Array.Copy(_file, start, bytes, 0, len);
        return bytes;
    }
}

/// <summary>A seekable stream that records every (offset, length) write — to prove direct-to-offset writes.</summary>
internal sealed class RecordingStream : Stream
{
    private readonly MemoryStream _inner = new();
    public List<(long Offset, int Length)> Writes { get; } = new();

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (Writes) Writes.Add((_inner.Position, count));
        _inner.Write(buffer, offset, count);
    }

    public byte[] ToArray() => _inner.ToArray();
}
