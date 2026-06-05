// SPDX-License-Identifier: MIT

using AetherMesh.Constants;
using AetherMesh.Content.Models;

namespace AetherMesh.Content;

/// <summary>
/// Coordinates parallel multi-peer chunk download using the <em>Chunk Shuffle</em>
/// (Self-Assembling Peer Interleaving) algorithm.
///
/// <para>
/// <b>Strategy:</b> random non-overlapping range assignment. When a peer's bitmap
/// arrives, the coordinator selects a random subset of chunks that peer has but we
/// lack, ensuring no two peers receive overlapping requests for the same chunk.
/// This beats sequential allocation (avoids hot spots) and rarest-first (too
/// expensive at small BLE peer counts of 2–8) while degrading gracefully if a
/// peer drops — its in-flight assignments are returned to the pending pool and
/// redistributed immediately.
/// </para>
///
/// <para>
/// <b>Bitmap coalescing:</b> callers check <see cref="OnChunkReceived"/> to learn
/// when to re-broadcast their availability bitmap. The coordinator uses
/// <em>event-driven coalescing</em>: re-advertise after
/// <see cref="ProtocolConstants.ChunkBitmapBroadcastBatchSize"/> chunks (a full
/// batch), OR after <see cref="ProtocolConstants.ChunkBitmapBroadcastCoalesceMs"/>
/// milliseconds with at least one new chunk — whichever fires first. This
/// eliminates per-chunk radio chatter while keeping peers informed in near
/// real-time.
/// </para>
///
/// <para>
/// This class is fully synchronous and dependency-free; the caller
/// (<see cref="ContentService"/>) drives all I/O.  Thread-safe via an exclusive
/// lock — contention is negligible as operations are O(N) in chunk count with no
/// blocking I/O inside the lock.
/// </para>
/// </summary>
public sealed class ChunkShuffleSession
{
    private readonly Lock _lock = new();
    private readonly int _chunkCount;
    private readonly Dictionary<string, bool[]>     _peerBitmaps      = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint>        _peerGenerations  = new(StringComparer.Ordinal);
    private readonly HashSet<int>                    _localHave        = [];
    private readonly Dictionary<int, string>         _inFlight         = [];   // chunkIndex → peerUhid
    private readonly Func<long>                      _getTimestampMs;

    private int  _chunksSinceLastBitmap;
    private long _lastBitmapTimestampMs;

    /// <summary>Root hash this session is tracking.</summary>
    public string RootHash { get; }

    /// <summary>Total number of chunks in the content.</summary>
    public int ChunkCount => _chunkCount;

    /// <summary>Returns <c>true</c> once every chunk is present in the local store.</summary>
    public bool IsComplete { get; private set; }

    /// <param name="rootHash">Content root hash (hex SHA-256).</param>
    /// <param name="chunkCount">Total number of chunks in the content.</param>
    /// <param name="localHave">
    ///   Chunk indices already present in the local store at session creation time.
    ///   Pass <c>null</c> or empty for a fresh download with nothing pre-cached.
    ///   Pass all indices for a seeder that just published the content.
    /// </param>
    /// <param name="getTimestampMs">
    ///   Timestamp provider (milliseconds). Defaults to <see cref="Environment.TickCount64"/>.
    ///   Inject a controlled provider in tests to drive coalescing time-outs deterministically.
    /// </param>
    public ChunkShuffleSession(
        string rootHash,
        int chunkCount,
        IEnumerable<int>? localHave  = null,
        Func<long>?       getTimestampMs = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootHash);
        if (chunkCount < 0) throw new ArgumentOutOfRangeException(nameof(chunkCount));

        RootHash          = rootHash;
        _chunkCount       = chunkCount;
        _getTimestampMs   = getTimestampMs ?? (() => Environment.TickCount64);
        _lastBitmapTimestampMs = _getTimestampMs();

        if (localHave is not null)
            foreach (var i in localHave)
                _localHave.Add(i);

        IsComplete = _localHave.Count >= chunkCount && chunkCount > 0;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Update a peer's known chunk bitmap and return the set of
    /// <c>(peerUhid, chunkIndices[])</c> assignments the caller should request.
    ///
    /// <para>
    /// Stale updates (generation ≤ latest seen for this peer) are silently
    /// discarded and return an empty list.  Each assignment list is already
    /// deduplicated: no chunk index appears in more than one assignment across
    /// all peers.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string PeerUhid, int[] ChunkIndices)> OnPeerBitmap(
        string peerUhid, bool[] peerHas, uint generation)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(peerHas);

        lock (_lock)
        {
            // Monotonic generation guard — discard older snapshots
            if (_peerGenerations.TryGetValue(peerUhid, out var lastGen))
            {
                // Unsigned comparison handles wrap-around correctly for the
                // practical range (no session runs 4 billion broadcasts).
                if (generation <= lastGen)
                    return Array.Empty<(string, int[])>();
            }

            _peerGenerations[peerUhid] = generation;
            _peerBitmaps[peerUhid]     = peerHas;

            return IsComplete ? Array.Empty<(string, int[])>() : ComputeAssignments();
        }
    }

    /// <summary>
    /// Record that chunk <paramref name="chunkIndex"/> was successfully received
    /// and verified. Returns <c>true</c> when the caller should re-broadcast its
    /// availability bitmap (batch of <see cref="ProtocolConstants.ChunkBitmapBroadcastBatchSize"/>
    /// reached, OR coalescing window of <see cref="ProtocolConstants.ChunkBitmapBroadcastCoalesceMs"/>
    /// ms elapsed since the last broadcast with at least one pending chunk).
    /// </summary>
    public bool OnChunkReceived(int chunkIndex)
    {
        lock (_lock)
        {
            _inFlight.Remove(chunkIndex);
            _localHave.Add(chunkIndex);
            _chunksSinceLastBitmap++;

            if (_localHave.Count >= _chunkCount && _chunkCount > 0)
                IsComplete = true;

            var now         = _getTimestampMs();
            var elapsed     = now - _lastBitmapTimestampMs;
            var batchFull   = _chunksSinceLastBitmap >= ProtocolConstants.ChunkBitmapBroadcastBatchSize;
            var timedOut    = elapsed >= ProtocolConstants.ChunkBitmapBroadcastCoalesceMs;

            if (batchFull || timedOut)
            {
                _chunksSinceLastBitmap = 0;
                _lastBitmapTimestampMs = now;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Record that a peer has disconnected or timed out. Releases all of its
    /// in-flight chunk assignments back to the pending pool, then recomputes
    /// assignments for the remaining connected peers.
    /// Returns the new <c>(peerUhid, chunkIndices[])</c> assignments to issue.
    /// </summary>
    public IReadOnlyList<(string PeerUhid, int[] ChunkIndices)> OnPeerDropped(string peerUhid)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);

        lock (_lock)
        {
            // Return in-flight chunks belonging to the dropped peer to the pool
            var released = _inFlight
                .Where(kvp => string.Equals(kvp.Value, peerUhid, StringComparison.Ordinal))
                .Select(kvp => kvp.Key)
                .ToArray();

            foreach (var idx in released)
                _inFlight.Remove(idx);

            _peerBitmaps.Remove(peerUhid);
            _peerGenerations.Remove(peerUhid);

            return IsComplete ? Array.Empty<(string, int[])>() : ComputeAssignments();
        }
    }

    /// <summary>
    /// Build the <see cref="ChunkBitmapPayload"/> representing our current local
    /// chunk availability.  <paramref name="generation"/> should be monotonically
    /// increasing; callers typically get this from an <see cref="Interlocked"/>
    /// counter on <see cref="ContentService"/>.
    /// </summary>
    public ChunkBitmapPayload BuildBitmapPayload(uint generation)
    {
        lock (_lock)
        {
            var flags = new bool[_chunkCount];
            foreach (var i in _localHave)
                if (i >= 0 && i < _chunkCount)
                    flags[i] = true;

            return new ChunkBitmapPayload
            {
                RootHash   = RootHash,
                ChunkCount = _chunkCount,
                HaveBitset = ChunkBitmapPayload.Encode(flags),
                Generation = generation,
            };
        }
    }

    /// <summary>
    /// Returns the count of chunks already held locally. Useful for progress
    /// reporting without exposing the internal <c>HashSet</c>.
    /// </summary>
    public int LocalHaveCount
    {
        get { lock (_lock) { return _localHave.Count; } }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Core assignment algorithm.  For every peer that has chunks we lack and is
    /// below the per-peer in-flight cap, randomly select a non-overlapping subset
    /// of candidates and mark them in-flight.  Caller holds <see cref="_lock"/>.
    /// </summary>
    private List<(string PeerUhid, int[] ChunkIndices)> ComputeAssignments()
    {
        // Build a per-peer in-flight count from the current dictionary snapshot.
        var inFlightPerPeer = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var peerUhid in _inFlight.Values)
        {
            inFlightPerPeer.TryGetValue(peerUhid, out var count);
            inFlightPerPeer[peerUhid] = count + 1;
        }

        var assignments = new List<(string, int[])>(_peerBitmaps.Count);

        foreach (var (peerUhid, peerHas) in _peerBitmaps)
        {
            inFlightPerPeer.TryGetValue(peerUhid, out var currentInflight);
            var slots = ProtocolConstants.MaxConcurrentChunkTransfers - currentInflight;
            if (slots <= 0) continue;

            // Candidates: peer holds it, we don't, it's not already in-flight to ANY peer
            var candidates = new List<int>(Math.Min(peerHas.Length, _chunkCount));
            var limit = Math.Min(peerHas.Length, _chunkCount);
            for (var i = 0; i < limit; i++)
            {
                if (peerHas[i] && !_localHave.Contains(i) && !_inFlight.ContainsKey(i))
                    candidates.Add(i);
            }

            if (candidates.Count == 0) continue;

            // Fisher-Yates partial shuffle — randomise, then take first <slots> elements.
            // This gives uniform selection without the overhead of sorting by rarity.
            FisherYatesPartial(candidates, slots);
            var toRequest = candidates.Take(slots).ToArray();

            foreach (var idx in toRequest)
                _inFlight[idx] = peerUhid;

            assignments.Add((peerUhid, toRequest));
        }

        return assignments;
    }

    /// <summary>
    /// Fisher-Yates partial shuffle: randomise only the first <paramref name="count"/>
    /// positions of <paramref name="list"/> using <see cref="Random.Shared"/>.
    /// Sufficient to get a uniformly random subset of size <paramref name="count"/>.
    /// </summary>
    private static void FisherYatesPartial<T>(List<T> list, int count)
    {
        var n   = list.Count;
        var rng = Random.Shared;
        for (var i = 0; i < count && i < n - 1; i++)
        {
            var j = rng.Next(i, n);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
