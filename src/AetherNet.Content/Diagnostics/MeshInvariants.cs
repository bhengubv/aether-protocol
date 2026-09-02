// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using AetherNet.Dtn;
using AetherNet.Models;

namespace AetherNet.Content.Diagnostics;

/// <summary>
/// Runtime predicates that mirror the safety + liveness properties proved
/// by the Petri net models in <c>aether-protocol/formal/</c>. Designed for
/// xUnit assertions: integration tests across any AetherNet consumer
/// (AetherMedia, Bruh!, SDPKT, txtMe!, third-party) call these after
/// exercising the integration, so the model and the code stay coupled.
///
/// <para>
/// Each predicate maps to a specific formal model. Five predicates were
/// promoted from <c>AetherMedia.LocalLibrary</c> to this protocol-shared
/// namespace in v1.3.0 (Wave 18) — the "core AetherNet functions belong on
/// AetherNet" rule. Three more predicates were added at the same time to
/// close <c>02_REMAINING_WORK.md</c> §10:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="DtnCustodyEventuallyTerminates"/> ←
///     <c>formal/dtn-custody</c>: bundle reaches a terminal state
///     (Delivered / Expired / Failed) — never stuck Pending or InCustody
///     forever.</description></item>
///   <item><description><see cref="MultiDeviceSyncConverges"/> ←
///     <c>formal/multi-device-sync</c>: after both devices process the
///     same mutation set, their observable state is identical
///     (set-equality).</description></item>
///   <item><description><see cref="ContentBitmapEventuallyComplete"/> ←
///     <c>formal/content-bitmap</c>: every requested chunk eventually
///     arrives and verifies.</description></item>
///   <item><description><see cref="ForgeIntegrity"/> ←
///     <c>formal/forge-integrity</c>: cached payload bytes hash to the
///     recorded content hash.</description></item>
///   <item><description><see cref="StreamSequenceMonotonic"/> ←
///     <c>formal/stream-abr</c>: segment sequence numbers issued by a
///     publisher are strictly increasing.</description></item>
///   <item><description><see cref="WatchTogetherBoundedLatency"/> ← (new)
///     <c>formal/watch-together-timed</c>: follower position drift from
///     host (after RTT compensation) is bounded.</description></item>
///   <item><description><see cref="OutboxBounded"/> ← (new)
///     <c>formal/outbox-backpressure</c>: outbox queue depth never exceeds
///     its configured cap; new work is rejected at the limit rather than
///     unbounded growth.</description></item>
///   <item><description><see cref="ByzantineQuorumReached"/> ← (new)
///     <c>formal/byzantine-routing</c>: agreement requires (N - f) peers
///     reporting the same value, where f is the byzantine-fault tolerance
///     (default f = N/3). Gates cover-art / lyric / news-source trust.
///     </description></item>
/// </list>
/// </summary>
public static class MeshInvariants
{
    // ─── DTN custody ────────────────────────────────────────────────────

    /// <summary>
    /// DTN custody: no bundle remains active forever. Returns true iff
    /// every bundle reaches a terminal state (Delivered / Expired / Failed)
    /// within the given deadline.
    /// </summary>
    public static async Task<bool> DtnCustodyEventuallyTerminates(
        IDtnService dtn,
        Func<Task> driveDelivery,
        int maxScans = 10,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dtn);
        ArgumentNullException.ThrowIfNull(driveDelivery);
        for (var i = 0; i < maxScans; i++)
        {
            var active = await dtn.GetActiveBundlesAsync(ct).ConfigureAwait(false);
            var stuck = active.Where(b => b.Status is BundleStatus.Pending or BundleStatus.InCustody).ToList();
            if (stuck.Count == 0) return true;
            await driveDelivery().ConfigureAwait(false);
        }
        var remaining = await dtn.GetActiveBundlesAsync(ct).ConfigureAwait(false);
        return !remaining.Any(b => b.Status is BundleStatus.Pending or BundleStatus.InCustody);
    }

    // ─── Multi-device sync ──────────────────────────────────────────────

    /// <summary>
    /// Multi-device sync: device B's observed state matches device A's
    /// after the same mutation set has been applied (set-equality, not
    /// list-equality — order need not match).
    /// </summary>
    public static bool MultiDeviceSyncConverges<T>(IEnumerable<T> deviceA, IEnumerable<T> deviceB) =>
        new HashSet<T>(deviceA).SetEquals(deviceB);

    // ─── Content bitmap ─────────────────────────────────────────────────

    /// <summary>
    /// Content bitmap: returns true iff every chunk of <paramref name="rootHash"/>
    /// has been verified locally after at most <paramref name="maxScans"/>
    /// attempts of <paramref name="driveDelivery"/>.
    /// </summary>
    public static async Task<bool> ContentBitmapEventuallyComplete(
        IContentService content,
        string rootHash,
        int expectedChunks,
        Func<Task> driveDelivery,
        int maxScans = 10,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(rootHash);
        ArgumentNullException.ThrowIfNull(driveDelivery);
        for (var i = 0; i < maxScans; i++)
        {
            var assembled = await content.AssembleAsync(rootHash, ct).ConfigureAwait(false);
            if (assembled is not null) return true;
            await driveDelivery().ConfigureAwait(false);
        }
        return await content.AssembleAsync(rootHash, ct).ConfigureAwait(false) is not null;
    }

    // ─── Forge integrity ────────────────────────────────────────────────

    /// <summary>
    /// Forge integrity: returns true iff <paramref name="payload"/>'s SHA-256
    /// hash (hex, uppercase or lowercase) matches <paramref name="expectedHashHex"/>.
    /// Self-contained — no dependency on any consumer's hashing helper.
    /// </summary>
    public static bool ForgeIntegrity(byte[] payload, string expectedHashHex)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(expectedHashHex);
        var actual = Convert.ToHexString(SHA256.HashData(payload));
        return string.Equals(actual, expectedHashHex, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Stream sequence ────────────────────────────────────────────────

    /// <summary>
    /// Stream sequence: returns true iff every published sequence number
    /// is strictly greater than its predecessor.
    /// </summary>
    public static bool StreamSequenceMonotonic(IEnumerable<uint> publishedSequences)
    {
        ArgumentNullException.ThrowIfNull(publishedSequences);
        uint? prev = null;
        foreach (var s in publishedSequences)
        {
            if (prev is not null && s <= prev.Value) return false;
            prev = s;
        }
        return true;
    }

    // ─── (New, v1.3.0) Watch-together bounded latency ───────────────────

    /// <summary>
    /// Watch-together timing: returns true iff every follower's reported
    /// position (already RTT-compensated by the caller) is within
    /// <paramref name="toleranceMs"/> of the host's authoritative position
    /// at the same wall-clock instant.
    ///
    /// <para>
    /// Maps to <c>formal/watch-together-timed</c>: under the Petri-net model
    /// the follower's position can drift from host by at most (RTT/2 + jitter);
    /// this runtime predicate gates per-follower drift in tests and at
    /// runtime quality monitors.
    /// </para>
    /// </summary>
    /// <param name="hostPositionMs">Host's authoritative playback position in milliseconds.</param>
    /// <param name="followerPositionsAfterRttCompensationMs">Follower positions in ms,
    /// already RTT-compensated by the caller (i.e. <c>positionMs + elapsed × playbackSpeed</c>).</param>
    /// <param name="toleranceMs">Maximum acceptable absolute drift in ms (default 100 ms).</param>
    public static bool WatchTogetherBoundedLatency(
        long hostPositionMs,
        IEnumerable<long> followerPositionsAfterRttCompensationMs,
        long toleranceMs = 100)
        // Moved to AetherNet.Core (AetherNet.Diagnostics.MeshInvariants) so that the Streaming layer —
        // which references Core but not Content — can call it at its follower-drift seam. Forwarded here
        // for backward compatibility.
        => AetherNet.Diagnostics.MeshInvariants.WatchTogetherBoundedLatency(
            hostPositionMs, followerPositionsAfterRttCompensationMs, toleranceMs);

    // ─── (New, v1.3.0) Outbox backpressure ──────────────────────────────

    /// <summary>
    /// Outbox backpressure: returns true iff the outbox queue depth has
    /// not exceeded <paramref name="maxQueueDepth"/>. When the queue is at
    /// capacity, new work should be rejected (the predicate fails on the
    /// next sample) rather than the queue growing unboundedly.
    ///
    /// <para>
    /// Maps to <c>formal/outbox-backpressure</c>: under the Petri-net model
    /// when arrival rate exceeds drain rate, the queue is bounded by an
    /// explicit cap. Used for scrobble / podcast download / chunk request
    /// queues across AetherMedia and any consumer doing background fan-out.
    /// </para>
    /// </summary>
    /// <param name="currentQueueDepth">Current outbox queue depth (in items, bytes — caller-defined unit).</param>
    /// <param name="maxQueueDepth">Configured cap; the predicate fails as soon as depth exceeds this.</param>
    public static bool OutboxBounded(int currentQueueDepth, int maxQueueDepth)
        // Moved to AetherNet.Core (AetherNet.Diagnostics.MeshInvariants); forwarded for compatibility.
        => AetherNet.Diagnostics.MeshInvariants.OutboxBounded(currentQueueDepth, maxQueueDepth);

    // ─── (New, v1.3.0) Byzantine routing quorum ─────────────────────────

    /// <summary>
    /// Byzantine routing quorum: returns true iff at least (N - f) peers
    /// among <paramref name="votes"/> reported the same value, where f is
    /// the byzantine-fault tolerance threshold (default f = N/3).
    ///
    /// <para>
    /// Maps to <c>formal/byzantine-routing</c>: under the Petri-net model
    /// up to f peers may report adversarial values; the runtime predicate
    /// checks the supermajority condition before accepting a result. Used
    /// to gate cover-art / lyric / news-source / route-reply trust.
    /// </para>
    ///
    /// <para>
    /// When the predicate returns true, <paramref name="agreedValue"/> holds
    /// the agreed-upon value. When false, it holds the modal value (most
    /// frequent observed) for diagnostics, but should NOT be acted on.
    /// </para>
    /// </summary>
    /// <param name="votes">Observed values, one per peer (duplicates indicate agreement).</param>
    /// <param name="agreedValue">Out: the agreed value when quorum is reached, else the modal value.</param>
    /// <param name="faultTolerance">Byzantine tolerance f (default -1 → f = N/3). Must satisfy f &lt; N.</param>
    public static bool ByzantineQuorumReached<T>(
        IEnumerable<T> votes,
        out T? agreedValue,
        int faultTolerance = -1)
        // Moved to AetherNet.Core (AetherNet.Diagnostics.MeshInvariants); forwarded for compatibility.
        => AetherNet.Diagnostics.MeshInvariants.ByzantineQuorumReached(votes, out agreedValue, faultTolerance);
}
