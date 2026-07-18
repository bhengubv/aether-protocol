// SPDX-License-Identifier: MIT

using AetherNet.Content.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Content.Download;

/// <summary>Tuning for <see cref="SegmentedContentDownloader"/>.</summary>
public sealed class SegmentedDownloadOptions
{
    /// <summary>Concurrent fetches to start with.</summary>
    public int InitialParallelism { get; init; } = 4;

    /// <summary>Lower bound the adaptive controller will not go below.</summary>
    public int MinParallelism { get; init; } = 1;

    /// <summary>Ceiling the adaptive controller will not exceed.</summary>
    public int MaxParallelism { get; init; } = 16;

    /// <summary>Transient-failure retries per chunk before the failure becomes permanent.</summary>
    public int MaxRetriesPerChunk { get; init; } = 5;

    /// <summary>Base delay for exponential backoff between retries.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>When true, the controller raises/lowers parallelism based on observed throughput.</summary>
    public bool EnableAdaptiveAcceleration { get; init; } = true;

    /// <summary>How often the adaptive controller samples throughput.</summary>
    public TimeSpan AccelerationInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Optional peer hint passed through to the chunk source.</summary>
    public string? PreferredPeer { get; init; }
}

/// <summary>Outcome of a completed <see cref="SegmentedContentDownloader.DownloadAsync"/> run.</summary>
public sealed class DownloadResult
{
    public int ChunksFetched { get; init; }
    public int ChunksResumed { get; init; }
    public int Retries { get; init; }
    public int SegmentSteals { get; init; }
    public int MaxObservedParallelism { get; init; }
}

/// <summary>Thrown when a segmented download cannot complete (permanent failure or retries exhausted).</summary>
public sealed class ContentDownloadException : Exception
{
    public ContentDownloadException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// An active, concurrent, resumable content downloader — the improvements the packet-driven
/// <see cref="ContentService"/> lacks, borrowed from Ghost Downloader 3:
/// <list type="bullet">
///   <item>concurrent segmented fetching across a bounded worker pool;</item>
///   <item>dynamic re-splitting — an idle worker steals the back half of the busiest segment;</item>
///   <item>an adaptive controller that raises parallelism while throughput improves and backs off when it doesn't;</item>
///   <item>direct-to-offset writes into a preallocated stream — no whole-file buffer, no final merge;</item>
///   <item>resume — chunks already in the store are written straight through, never re-fetched;</item>
///   <item>transient-vs-permanent retry with exponential backoff;</item>
///   <item>one error boundary — any fatal failure cancels the run and surfaces as a single exception.</item>
/// </list>
/// Verified chunks are also persisted to the <see cref="IContentStore"/> so a later run resumes.
/// </summary>
public sealed class SegmentedContentDownloader
{
    private readonly IContentStore _store;
    private readonly IChunkSource _source;
    private readonly SegmentedDownloadOptions _options;
    private readonly ILogger _logger;

    public SegmentedContentDownloader(
        IContentStore store,
        IChunkSource source,
        SegmentedDownloadOptions? options = null,
        ILogger<SegmentedContentDownloader>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? new SegmentedDownloadOptions();
        _logger = logger ?? NullLogger<SegmentedContentDownloader>.Instance;

        if (_options.MaxParallelism < 1) throw new ArgumentException("MaxParallelism must be >= 1");
        if (_options.InitialParallelism < 1) throw new ArgumentException("InitialParallelism must be >= 1");
    }

    /// <summary>
    /// Download every chunk of <paramref name="descriptor"/> into <paramref name="destination"/>
    /// (preallocated + written at each chunk's offset). Chunks already in the store are resumed.
    /// Throws <see cref="ContentDownloadException"/> on unrecoverable failure.
    /// </summary>
    public Task<DownloadResult> DownloadAsync(ContentDescriptor descriptor, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("destination must be writable and seekable", nameof(destination));
        return new Run(this, descriptor, destination).ExecuteAsync(cancellationToken);
    }

    private sealed class Segment
    {
        public int Next;   // next position (into the missing[] array) to claim
        public int End;    // exclusive end position
        public int Remaining => End - Next;
    }

    private sealed class Run
    {
        private readonly SegmentedContentDownloader _owner;
        private readonly ContentDescriptor _descriptor;
        private readonly Stream _destination;
        private readonly SegmentedDownloadOptions _opt;

        private readonly object _segLock = new();
        private readonly object _writeLock = new();
        private readonly List<Segment> _segments = new();
        private int[] _missing = Array.Empty<int>();

        private int _fetched;
        private int _retries;
        private int _steals;
        private int _resumed;

        private readonly SemaphoreSlim _gate;
        private int _permits;
        private int _maxObservedPermits;

        public Run(SegmentedContentDownloader owner, ContentDescriptor descriptor, Stream destination)
        {
            _owner = owner;
            _descriptor = descriptor;
            _destination = destination;
            _opt = owner._options;
            _permits = Math.Clamp(_opt.InitialParallelism, _opt.MinParallelism, _opt.MaxParallelism);
            _maxObservedPermits = _permits;
            _gate = new SemaphoreSlim(_permits, _opt.MaxParallelism);
        }

        public async Task<DownloadResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            _destination.SetLength(_descriptor.TotalBytes); // preallocate — no growth during writes

            // ── Resume: chunks already held are written straight through, never re-fetched ──
            var have = new HashSet<int>(await _owner._store.ListChunksAsync(_descriptor.RootHash, cancellationToken).ConfigureAwait(false));
            foreach (var i in have.OrderBy(x => x))
            {
                if ((uint)i >= (uint)_descriptor.ChunkCount) continue;
                var bytes = await _owner._store.GetChunkAsync(_descriptor.RootHash, i, cancellationToken).ConfigureAwait(false);
                if (bytes is null) continue;
                WriteAt(i, bytes);
                _resumed++;
            }

            _missing = Enumerable.Range(0, _descriptor.ChunkCount).Where(i => !have.Contains(i)).ToArray();
            if (_missing.Length == 0)
                return Snapshot();

            // ── Segment the missing set into contiguous ranges, one per initial worker ──
            int workerCount = Math.Min(_permits, _missing.Length);
            int per = (_missing.Length + workerCount - 1) / workerCount;
            for (int start = 0; start < _missing.Length; start += per)
                _segments.Add(new Segment { Next = start, End = Math.Min(start + per, _missing.Length) });

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var workers = new Task[_opt.MaxParallelism];
            for (int w = 0; w < workers.Length; w++)
                workers[w] = Task.Run(() => WorkerAsync(linked), linked.Token);

            Task? monitor = _opt.EnableAdaptiveAcceleration
                ? Task.Run(() => AccelerationMonitorAsync(linked.Token), linked.Token)
                : null;

            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // caller cancelled — propagate as-is
            }
            catch (ContentDownloadException)
            {
                if (!linked.IsCancellationRequested) linked.Cancel();
                throw; // already the boundary exception — surface as-is
            }
            catch (Exception ex)
            {
                if (!linked.IsCancellationRequested) linked.Cancel();
                throw new ContentDownloadException(
                    $"segmented download of {_descriptor.RootHash} failed: {ex.Message}", ex);
            }
            finally
            {
                if (!linked.IsCancellationRequested) linked.Cancel(); // wind the monitor down
                if (monitor is not null)
                {
                    try { await monitor.ConfigureAwait(false); } catch { /* monitor is best-effort */ }
                }
            }

            return Snapshot();
        }

        private async Task WorkerAsync(CancellationTokenSource linked)
        {
            var ct = linked.Token;
            Segment? mine = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int position;
                lock (_segLock)
                {
                    if (mine is not null && mine.Remaining > 0)
                    {
                        position = mine.Next++;
                    }
                    else
                    {
                        // Steal the back half of the busiest segment (dynamic re-split)…
                        var victim = _segments
                            .Where(s => !ReferenceEquals(s, mine) && s.Remaining > 1)
                            .OrderByDescending(s => s.Remaining)
                            .FirstOrDefault();
                        if (victim is not null)
                        {
                            int mid = victim.Next + victim.Remaining / 2;
                            mine = new Segment { Next = mid, End = victim.End };
                            victim.End = mid;
                            _segments.Add(mine);
                            _steals++;
                            position = mine.Next++;
                        }
                        else
                        {
                            // …or grab a single remaining chunk from any segment to drain the tail.
                            var any = _segments.Where(s => s.Remaining > 0).OrderByDescending(s => s.Remaining).FirstOrDefault();
                            if (any is null) return; // all work claimed
                            position = any.Next++;
                            mine = null;
                        }
                    }
                }

                int chunkIndex = _missing[position];
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await FetchVerifyWriteAsync(chunkIndex, linked, ct).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        private async Task FetchVerifyWriteAsync(int chunkIndex, CancellationTokenSource linked, CancellationToken ct)
        {
            int attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bytes = await _owner._source
                        .FetchChunkAsync(_descriptor.RootHash, chunkIndex, _opt.PreferredPeer, ct)
                        .ConfigureAwait(false);

                    if (bytes is null || !_descriptor.VerifyChunk(chunkIndex, bytes))
                        throw new ChunkSourceException($"chunk {chunkIndex} failed hash verification", permanent: false);

                    WriteAt(chunkIndex, bytes);
                    await _owner._store.SaveChunkAsync(_descriptor.RootHash, chunkIndex, bytes, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _fetched);
                    return;
                }
                catch (ChunkSourceException ex) when (!ex.Permanent && attempt < _opt.MaxRetriesPerChunk)
                {
                    attempt++;
                    Interlocked.Increment(ref _retries);
                    var delay = TimeSpan.FromMilliseconds(_opt.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (ChunkSourceException ex)
                {
                    if (!linked.IsCancellationRequested) linked.Cancel();
                    throw new ContentDownloadException(
                        $"chunk {chunkIndex} of {_descriptor.RootHash} could not be fetched: {ex.Message}", ex);
                }
            }
        }

        private void WriteAt(int chunkIndex, byte[] bytes)
        {
            long offset = (long)chunkIndex * _descriptor.ChunkSizeBytes;
            lock (_writeLock)
            {
                _destination.Seek(offset, SeekOrigin.Begin);
                _destination.Write(bytes, 0, bytes.Length);
            }
        }

        private async Task AccelerationMonitorAsync(CancellationToken ct)
        {
            int lastFetched = 0, lastDelta = -1;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(_opt.AccelerationInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                int now = Volatile.Read(ref _fetched);
                int delta = now - lastFetched;
                lastFetched = now;

                if (lastDelta < 0) { lastDelta = delta; continue; }

                if (delta >= lastDelta && _permits < _opt.MaxParallelism)
                {
                    // Throughput holding or improving with headroom → add a concurrent fetch.
                    _gate.Release();
                    _permits++;
                    if (_permits > _maxObservedPermits) _maxObservedPermits = _permits;
                }
                else if (delta < lastDelta && _permits > _opt.MinParallelism)
                {
                    // Throughput fell → hold a permit back (fewer concurrent fetches).
                    try { await _gate.WaitAsync(ct).ConfigureAwait(false); _permits--; }
                    catch (OperationCanceledException) { return; }
                }
                lastDelta = delta;
            }
        }

        private DownloadResult Snapshot() => new()
        {
            ChunksFetched = _fetched,
            ChunksResumed = _resumed,
            Retries = _retries,
            SegmentSteals = _steals,
            MaxObservedParallelism = _maxObservedPermits,
        };
    }
}
