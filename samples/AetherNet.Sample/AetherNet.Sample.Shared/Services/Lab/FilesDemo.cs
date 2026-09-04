// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Security.Cryptography;
using AetherNet.Content;
using AetherNet.Content.Download;
using AetherNet.Content.Models;
using AetherNet.Protocol;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Chunked mesh content distribution and multi-peer parallel download, standing up a real in-process
/// mesh. A blob is content-addressed into a <see cref="ContentDescriptor"/> (SHA-256 per chunk, a
/// SHA-256 root over the chunk hashes), then split across two seeders that each hold a disjoint half —
/// so a completed download <em>proves</em> both peers were pulled from at once. The leecher runs the
/// real <see cref="SegmentedContentDownloader"/> over a <see cref="MeshChunkSource"/>: concurrent
/// segmented fetching, work-stealing, adaptive parallelism, per-chunk verification, direct-to-offset
/// reassembly. Only the radio is simulated (an in-process byte transport); everything else is the
/// shipping code.
/// </summary>
public sealed class FilesDemo : IDisposable
{
    private const int ChunkSize = 1024; // small, so a modest blob yields a dense, legible chunk map

    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();

    private byte[]? _original;
    private ContentDescriptor? _descriptor;

    // Live progress on the leecher: which chunks have arrived and verified.
    private bool[] _have = Array.Empty<bool>();

    private readonly List<IDisposable> _disposables = new();

    public event Action? Changed;

    public PublishView? Published { get; private set; }
    public DownloadReport? Report { get; private set; }
    public ShuffleView? Shuffle { get; private set; }
    public bool Running { get; private set; }

    /// <summary>Chunk index → which seeder holds it (even = A, odd = B). Drives the coloured bitmap.</summary>
    public string HolderOf(int chunkIndex) => (chunkIndex & 1) == 0 ? "A" : "B";

    public bool[] HaveSnapshot()
    {
        lock (_gate) return (bool[])_have.Clone();
    }

    public IReadOnlyList<LogLine> Log
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    // ── 1. Publish: content-address a blob into a descriptor ─────────────────────

    /// <summary>Generate a <paramref name="sizeKb"/>-KB blob and compute its <see cref="ContentDescriptor"/>.</summary>
    public void Publish(int sizeKb)
    {
        sizeKb = Math.Clamp(sizeKb, 1, 128);
        var bytes = new byte[sizeKb * 1024];
        Random.Shared.NextBytes(bytes);

        var descriptor = ContentDescriptor.FromBytes($"aether-blob-{sizeKb}k.bin", bytes, "application/octet-stream", ChunkSize);
        _original = bytes;
        _descriptor = descriptor;

        lock (_gate) _have = new bool[descriptor.ChunkCount];
        Report = null;

        Published = new PublishView(
            Name: descriptor.Name,
            TotalBytes: descriptor.TotalBytes,
            ChunkSize: descriptor.ChunkSizeBytes,
            ChunkCount: descriptor.ChunkCount,
            Root: descriptor.RootHash,
            FirstChunkHash: descriptor.ChunkHashes.Count > 0 ? descriptor.ChunkHashes[0] : "",
            SelfVerifies: descriptor.VerifySelf());

        Emit("publish", $"{descriptor.TotalBytes:N0} B → {descriptor.ChunkCount} chunks of {descriptor.ChunkSizeBytes} B, each SHA-256'd.");
        Emit("publish", $"root {Short(descriptor.RootHash)} is SHA-256 over the chunk hashes; the descriptor self-verifies: {descriptor.VerifySelf()}.");
        Emit("split", "even chunks → Seeder A, odd chunks → Seeder B. Neither can complete the file alone.");
        Raise();
    }

    // ── 2. Multi-peer parallel download over the mesh ────────────────────────────

    /// <summary>
    /// Build the mesh (Seeder A holds even chunks, Seeder B odd, Leecher none), then run the segmented
    /// downloader on the leecher. Every chunk request floods; only the seeder that holds it answers, so
    /// the two halves arrive in parallel from two different peers, are verified, and reassemble.
    /// </summary>
    public async Task RunDownloadAsync()
    {
        if (_original is null || _descriptor is null) Publish(24);
        var original = _original!;
        var descriptor = _descriptor!;

        Reset();
        Running = true;
        lock (_gate) _have = new bool[descriptor.ChunkCount];
        Raise();

        // Three nodes on the simulated network; unique uhids so re-runs never collide in the registry.
        var run = Guid.NewGuid().ToString("N")[..8];
        var seederA = NewNode($"lab:files:A:{run}");
        var seederB = NewNode($"lab:files:B:{run}");
        var leecher = NewNode($"lab:files:L:{run}");
        var all = new[] { seederA, seederB, leecher };

        // Fully-connected senders + one content-packet dispatcher per node.
        foreach (var self in all)
        foreach (var other in all)
            if (!ReferenceEquals(self, other))
                self.Sender.AddPotentialPeer(other.Uhid);
        foreach (var node in all)
        {
            var n = node;
            n.Transport.DataReceived += (_src, bytes) =>
            {
                MeshPacket packet;
                try { packet = PacketSerializer.Deserialize(bytes); }
                catch { return; }
                _ = n.Content.HandleAsync(packet);
            };
        }

        // Seed the two disjoint halves + hand every node the descriptor (as a mesh announcement would).
        await seederA.Store.SaveDescriptorAsync(descriptor);
        await seederB.Store.SaveDescriptorAsync(descriptor);
        await leecher.Store.SaveDescriptorAsync(descriptor);
        for (int i = 0; i < descriptor.ChunkCount; i++)
        {
            var chunk = Slice(original, i, descriptor);
            if ((i & 1) == 0) await seederA.Store.SaveChunkAsync(descriptor.RootHash, i, chunk);
            else await seederB.Store.SaveChunkAsync(descriptor.RootHash, i, chunk);
        }

        // Watch chunks land on the leecher, live.
        leecher.Content.ChunkReceived += OnLeecherChunk;

        var source = new MeshChunkSource(leecher.Content, leecher.Store, TimeSpan.FromSeconds(10));
        _disposables.Add(source);
        var downloader = new SegmentedContentDownloader(leecher.Store, source,
            new SegmentedDownloadOptions { InitialParallelism = 4, MaxParallelism = 12 });

        Emit("mesh", "3 nodes up. Leecher starts a segmented, multi-peer pull…");
        Raise();

        DownloadResult result;
        byte[] assembled;
        var sw = Stopwatch.StartNew();
        try
        {
            using var dest = new MemoryStream();
            result = await downloader.DownloadAsync(descriptor, dest);
            assembled = dest.ToArray();
        }
        catch (Exception ex)
        {
            Emit("mesh", $"download failed: {ex.GetType().Name} — {ex.Message}");
            Running = false;
            Raise();
            return;
        }
        finally
        {
            leecher.Content.ChunkReceived -= OnLeecherChunk;
            sw.Stop();
        }

        var integrity = assembled.Length == original.Length
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(assembled), SHA256.HashData(original));
        int fromA = Enumerable.Range(0, descriptor.ChunkCount).Count(i => (i & 1) == 0);
        int fromB = descriptor.ChunkCount - fromA;

        Report = new DownloadReport(
            ChunksFetched: result.ChunksFetched,
            ChunksResumed: result.ChunksResumed,
            Retries: result.Retries,
            SegmentSteals: result.SegmentSteals,
            MaxParallelism: result.MaxObservedParallelism,
            FromSeederA: fromA,
            FromSeederB: fromB,
            Verified: integrity,
            ElapsedMs: sw.ElapsedMilliseconds);

        Emit("mesh", $"done in {sw.ElapsedMilliseconds} ms: fetched {result.ChunksFetched} chunks — {fromA} even from A, {fromB} odd from B, in parallel.");
        Emit("mesh", $"work-steals {result.SegmentSteals}, peak concurrency {result.MaxObservedParallelism}, retries {result.Retries}.");
        Emit("verify", integrity
            ? "reassembled blob's SHA-256 equals the original's — every chunk verified on arrival. ✓"
            : "integrity check FAILED (unexpected).");

        Running = false;
        Reset(); // tear the mesh down; the report + bitmap stay on screen
        Raise();
    }

    private void OnLeecherChunk(object? sender, ChunkArrivedEventArgs e)
    {
        if (!e.Verified) return;
        lock (_gate)
            if ((uint)e.ChunkIndex < (uint)_have.Length)
                _have[e.ChunkIndex] = true;
        Raise();
    }

    // ── 3. Chunk-shuffle assignment (pure, synchronous) ──────────────────────────

    /// <summary>
    /// Feed the real <see cref="ChunkShuffleSession"/> two peer bitmaps and show it hand out
    /// non-overlapping random subsets — the Self-Assembling Peer Interleaving algorithm, in isolation.
    /// </summary>
    public void RunChunkShuffle()
    {
        var descriptor = _descriptor;
        if (descriptor is null) { Publish(24); descriptor = _descriptor!; }

        var session = new ChunkShuffleSession(descriptor.RootHash, descriptor.ChunkCount, localHave: Array.Empty<int>());

        var evenFlags = new bool[descriptor.ChunkCount];
        var oddFlags = new bool[descriptor.ChunkCount];
        for (int i = 0; i < descriptor.ChunkCount; i++)
            if ((i & 1) == 0) evenFlags[i] = true; else oddFlags[i] = true;

        var aAssign = session.OnPeerBitmap("Seeder-A", evenFlags, generation: 1);
        var bAssign = session.OnPeerBitmap("Seeder-B", oddFlags, generation: 1);

        int[] a = aAssign.Count > 0 ? aAssign[0].ChunkIndices : Array.Empty<int>();
        int[] b = bAssign.Count > 0 ? bAssign[0].ChunkIndices : Array.Empty<int>();
        bool disjoint = !a.Intersect(b).Any();

        Shuffle = new ShuffleView(a.OrderBy(x => x).ToArray(), b.OrderBy(x => x).ToArray(), disjoint);

        Emit("shuffle", $"A (has {evenFlags.Count(f => f)} even chunks) → assigned [{string.Join(",", a.OrderBy(x => x))}].");
        Emit("shuffle", $"B (has {oddFlags.Count(f => f)} odd chunks) → assigned [{string.Join(",", b.OrderBy(x => x))}].");
        Emit("shuffle", disjoint
            ? "the two assignments are disjoint, each capped at MaxConcurrentChunkTransfers (4) in-flight. ✓"
            : "assignments overlapped (unexpected).");
        Raise();
    }

    // ── Mesh plumbing ─────────────────────────────────────────────────────────────

    private Node NewNode(string uhid)
    {
        var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
        var sender = new InProcessMeshSender(uhid, transport);
        var store = new InMemoryContentStore();
        var content = new ContentService(sender, new NullRoutingService(), store);
        _disposables.Add(transport);
        return new Node(uhid, transport, sender, store, content);
    }

    private static byte[] Slice(byte[] data, int index, ContentDescriptor d)
    {
        int start = index * d.ChunkSizeBytes; // demo blobs are ≤128 KB, so this stays well within int
        int len = Math.Min(d.ChunkSizeBytes, data.Length - start);
        var chunk = new byte[len];
        Array.Copy(data, start, chunk, 0, len);
        return chunk;
    }

    /// <summary>Tear down the in-process nodes from a run (idempotent).</summary>
    private void Reset()
    {
        foreach (var d in _disposables)
            try { d.Dispose(); } catch { /* best-effort teardown */ }
        _disposables.Clear();
    }

    private static string Short(string hex) => hex.Length <= 12 ? hex : hex[..12] + "…";

    private void Emit(string who, string text)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(who, text));
            if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200);
        }
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose() => Reset();

    // ── View models ──────────────────────────────────────────────────────────────

    private sealed record Node(
        string Uhid, InProcessTransportService Transport, InProcessMeshSender Sender,
        InMemoryContentStore Store, ContentService Content);

    public sealed record LogLine(string Who, string Text);

    public sealed record PublishView(
        string Name, long TotalBytes, int ChunkSize, int ChunkCount, string Root,
        string FirstChunkHash, bool SelfVerifies);

    public sealed record DownloadReport(
        int ChunksFetched, int ChunksResumed, int Retries, int SegmentSteals, int MaxParallelism,
        int FromSeederA, int FromSeederB, bool Verified, long ElapsedMs);

    public sealed record ShuffleView(int[] AssignedA, int[] AssignedB, bool Disjoint);
}
