// SPDX-License-Identifier: MIT

namespace AetherNet.Diagnostics;

/// <summary>
/// Pure runtime invariant predicates that mirror safety + liveness properties proved by the Petri-net
/// models in <c>aether-protocol/formal/</c>. These live in <c>AetherNet.Core</c> — the lowest layer —
/// so that <b>any</b> service (Streaming, Transport, Messaging, the composition root's health checks)
/// can call them at its own runtime seam. They were previously stranded in
/// <c>AetherNet.Content.Diagnostics.MeshInvariants</c>, a layer above the services that needed them,
/// which is why they had test coverage but zero runtime callers.
///
/// <para>
/// The predicates are self-contained (no service dependencies): a caller samples its own state and
/// asks whether the invariant holds. <c>AetherNet.Content.Diagnostics.MeshInvariants</c> forwards to
/// these for backward compatibility, and keeps the model-coupled async predicates
/// (<c>DtnCustodyEventuallyTerminates</c>, <c>ContentBitmapEventuallyComplete</c>) that genuinely
/// need the Content layer's service interfaces.
/// </para>
/// </summary>
public static class MeshInvariants
{
    /// <summary>
    /// Watch-together timing: returns true iff every follower's reported position (already
    /// RTT-compensated by the caller) is within <paramref name="toleranceMs"/> of the host's
    /// authoritative position at the same wall-clock instant.
    ///
    /// <para>
    /// Maps to <c>formal/watch-together-timed</c>: under the Petri-net model the follower's position
    /// can drift from host by at most (RTT/2 + jitter); this predicate gates per-follower drift both
    /// in tests and at runtime quality monitors.
    /// </para>
    /// </summary>
    /// <param name="hostPositionMs">Host's authoritative playback position in milliseconds.</param>
    /// <param name="followerPositionsAfterRttCompensationMs">Follower positions in ms, already
    /// RTT-compensated by the caller (i.e. <c>positionMs + elapsed × playbackSpeed</c>).</param>
    /// <param name="toleranceMs">Maximum acceptable absolute drift in ms (default 100 ms).</param>
    public static bool WatchTogetherBoundedLatency(
        long hostPositionMs,
        IEnumerable<long> followerPositionsAfterRttCompensationMs,
        long toleranceMs = 100)
    {
        ArgumentNullException.ThrowIfNull(followerPositionsAfterRttCompensationMs);
        if (toleranceMs < 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceMs), "Tolerance must be non-negative.");
        foreach (var p in followerPositionsAfterRttCompensationMs)
        {
            if (Math.Abs(p - hostPositionMs) > toleranceMs) return false;
        }
        return true;
    }

    /// <summary>
    /// Outbox backpressure: returns true iff the outbox queue depth has not exceeded
    /// <paramref name="maxQueueDepth"/>. When the queue is at capacity, new work should be rejected
    /// (the predicate fails on the next sample) rather than the queue growing unboundedly.
    ///
    /// <para>
    /// Maps to <c>formal/outbox-backpressure</c>: under the Petri-net model when arrival rate exceeds
    /// drain rate, the queue is bounded by an explicit cap. Used for scrobble / podcast download /
    /// chunk request queues and any consumer doing background fan-out.
    /// </para>
    /// </summary>
    /// <param name="currentQueueDepth">Current outbox queue depth (items or bytes — caller-defined unit).</param>
    /// <param name="maxQueueDepth">Configured cap; the predicate fails as soon as depth exceeds this.</param>
    public static bool OutboxBounded(int currentQueueDepth, int maxQueueDepth)
    {
        if (maxQueueDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueueDepth), "Cap must be non-negative.");
        if (currentQueueDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(currentQueueDepth), "Depth cannot be negative.");
        return currentQueueDepth <= maxQueueDepth;
    }

    /// <summary>
    /// Byzantine routing quorum: returns true iff at least (N - f) peers among
    /// <paramref name="votes"/> reported the same value, where f is the byzantine-fault tolerance
    /// threshold (default f = N/3).
    ///
    /// <para>
    /// Maps to <c>formal/byzantine-routing</c>: up to f peers may report adversarial values; the
    /// predicate checks the supermajority condition before accepting a result. Provided for the trust
    /// gates that live in consumers (cover-art / lyric / news-source selection) — the protocol core's
    /// own routing uses single-source-signed RREPs (see <c>IRouteReplyVerifier</c>), not quorum, so it
    /// is not called on the core forward path.
    /// </para>
    ///
    /// <para>
    /// When the predicate returns true, <paramref name="agreedValue"/> holds the agreed-upon value.
    /// When false, it holds the modal value (most frequent observed) for diagnostics, but should NOT
    /// be acted on.
    /// </para>
    /// </summary>
    /// <param name="votes">Observed values, one per peer (duplicates indicate agreement).</param>
    /// <param name="agreedValue">Out: the agreed value when quorum is reached, else the modal value.</param>
    /// <param name="faultTolerance">Byzantine tolerance f (default -1 → f = N/3). Must satisfy f &lt; N.</param>
    public static bool ByzantineQuorumReached<T>(
        IEnumerable<T> votes,
        out T? agreedValue,
        int faultTolerance = -1)
    {
        ArgumentNullException.ThrowIfNull(votes);
        var voteList = votes.ToList();
        if (voteList.Count == 0)
        {
            agreedValue = default;
            return false;
        }

        var n = voteList.Count;
        var f = faultTolerance >= 0 ? faultTolerance : n / 3;
        if (f >= n)
        {
            // f cannot meet or exceed N — no possible quorum.
            agreedValue = default;
            return false;
        }
        var threshold = n - f;

        var winner = voteList
            .GroupBy(v => v, EqualityComparer<T>.Default)
            .OrderByDescending(g => g.Count())
            .First();

        agreedValue = winner.Key;
        return winner.Count() >= threshold;
    }
}
