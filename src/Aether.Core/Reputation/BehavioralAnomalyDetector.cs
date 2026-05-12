// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Aether.Reputation;

/// <summary>
/// Default implementation of <see cref="IAnomalyDetector"/>.
///
/// All observation methods are synchronous and lock-free at the per-node level —
/// they complete in O(1) amortized time. Reputation signals are fired with
/// fire-and-forget (<c>_ = ...</c>) to avoid blocking the hot path.
///
/// Thresholds (all configurable via <see cref="AnomalyDetectorOptions"/>):
/// <list type="bullet">
///   <item>Volume spike: current 30s count &gt; VolumeSpikeMultiplier (5×) × EWMA baseline</item>
///   <item>Destination scatter: &gt; DestinationScatterThreshold (50) unique dests in 60s</item>
///   <item>Geohash mismatch: claimed prefix ≠ observed prefix at GeohashPrefixLength (4) chars</item>
///   <item>SPK-sig failure: every failure emits immediately (no rate limiting)</item>
/// </list>
/// </summary>
public sealed class BehavioralAnomalyDetector : IAnomalyDetector
{
    // ── Tuneable thresholds ────────────────────────────────────────────────────────
    private readonly int _volumeWindowMs;          // 30 000
    private readonly double _volumeSpikeMultiplier;// 5.0
    private readonly double _ewmaAlpha;            // 0.20
    private readonly int _scatterWindowMs;         // 60 000
    private readonly int _scatterThreshold;        // 50
    private readonly int _geohashPrefixLength;     // 4  (~50 km)
    private readonly int _geohashRateLimitMs;      // 60 000 (min ms between geo-mismatch signals per node)

    // ── Per-node state ─────────────────────────────────────────────────────────────

    // Volume tracking: (windowStartMs, windowPacketCount, ewmaBaseline)
    private readonly ConcurrentDictionary<string, VolumeState> _volumeState = new(StringComparer.Ordinal);

    // Destination scatter: list of (destinationUhid, timestampMs) per source
    private readonly ConcurrentDictionary<string, ScatterState> _scatterState = new(StringComparer.Ordinal);

    // Geohash mismatch: last-signalled timestamp per node (for rate-limiting)
    private readonly ConcurrentDictionary<string, long> _geoLastSignal = new(StringComparer.Ordinal);

    private readonly INodeReputationService _reputation;

    /// <param name="reputation">Reputation service to emit signals into.</param>
    /// <param name="options">Optional threshold overrides; if null, defaults are used.</param>
    public BehavioralAnomalyDetector(
        INodeReputationService reputation,
        AnomalyDetectorOptions? options = null)
    {
        _reputation = reputation ?? throw new ArgumentNullException(nameof(reputation));
        var o = options ?? new AnomalyDetectorOptions();
        _volumeWindowMs          = o.VolumeWindowMs;
        _volumeSpikeMultiplier   = o.VolumeSpikeMultiplier;
        _ewmaAlpha               = o.EwmaAlpha;
        _scatterWindowMs         = o.ScatterWindowMs;
        _scatterThreshold        = o.ScatterThreshold;
        _geohashPrefixLength     = o.GeohashPrefixLength;
        _geohashRateLimitMs      = o.GeohashRateLimitMs;
    }

    /// <inheritdoc />
    public void ObservePacket(string sourceUhid, string destinationUhid, long timestampMs)
    {
        if (string.IsNullOrEmpty(sourceUhid)) return;

        ObserveVolumePacket(sourceUhid, timestampMs);
        ObserveScatterPacket(sourceUhid, destinationUhid, timestampMs);
    }

    /// <inheritdoc />
    public void ObserveGeohashClaim(string uhid, string claimedGeohash, string observedRoutingGeohash)
    {
        if (string.IsNullOrEmpty(uhid)) return;
        if (string.IsNullOrEmpty(claimedGeohash) || string.IsNullOrEmpty(observedRoutingGeohash)) return;

        // Compare at the configured prefix length (~50 km at 4 chars).
        var claimedPrefix  = ClampedPrefix(claimedGeohash,  _geohashPrefixLength);
        var observedPrefix = ClampedPrefix(observedRoutingGeohash, _geohashPrefixLength);
        if (string.Equals(claimedPrefix, observedPrefix, StringComparison.OrdinalIgnoreCase))
            return; // No mismatch.

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Rate-limit: emit at most once per _geohashRateLimitMs per node.
        var lastSignal = _geoLastSignal.GetOrAdd(uhid, _ => 0L);
        if (nowMs - lastSignal < _geohashRateLimitMs) return;
        if (!_geoLastSignal.TryUpdate(uhid, nowMs, lastSignal)) return; // Concurrent update lost; skip.

        // Geohash mismatch = identity spoofing level severity.
        _ = _reputation.RecordSignatureFailureAsync(uhid);
    }

    /// <inheritdoc />
    public void ObserveSpkSigFailure(string uhid)
    {
        if (string.IsNullOrEmpty(uhid)) return;
        _ = _reputation.RecordSignatureFailureAsync(uhid);
    }

    // ── Volume spike detection ────────────────────────────────────────────────────

    private void ObserveVolumePacket(string sourceUhid, long timestampMs)
    {
        _volumeState.AddOrUpdate(
            sourceUhid,
            _k => new VolumeState { WindowStartMs = timestampMs, WindowCount = 1, EwmaBaseline = 0 },
            (_k, state) =>
            {
                var age = timestampMs - state.WindowStartMs;
                if (age < _volumeWindowMs)
                {
                    // Still in the same window.
                    state.WindowCount++;
                }
                else
                {
                    // Window rolled. Update EWMA with the completed window's count.
                    var completedCount = state.WindowCount;
                    var newEwma = state.EwmaBaseline < 1
                        ? completedCount  // Warm-up: first window becomes the baseline.
                        : _ewmaAlpha * completedCount + (1.0 - _ewmaAlpha) * state.EwmaBaseline;
                    state.EwmaBaseline = newEwma;
                    state.WindowStartMs = timestampMs;
                    state.WindowCount = 1;
                }

                // Spike check: if EWMA is established and current window is spiky.
                if (state.EwmaBaseline >= 1 && state.WindowCount > _volumeSpikeMultiplier * state.EwmaBaseline)
                {
                    // Fire-and-forget; the spike penalty is −0.05 per detection.
                    _ = _reputation.RecordRreqFloodAttemptAsync(sourceUhid);

                    // Reset window count after signalling to avoid re-triggering every packet.
                    state.WindowCount = 0;
                }

                return state;
            });
    }

    // ── Destination scatter detection ─────────────────────────────────────────────

    private void ObserveScatterPacket(string sourceUhid, string destinationUhid, long timestampMs)
    {
        if (string.IsNullOrEmpty(destinationUhid)) return;

        _scatterState.AddOrUpdate(
            sourceUhid,
            _k =>
            {
                var s = new ScatterState();
                s.Entries.Add((destinationUhid, timestampMs));
                return s;
            },
            (_k, state) =>
            {
                lock (state)
                {
                    // Prune entries older than the scatter window.
                    var cutoff = timestampMs - _scatterWindowMs;
                    state.Entries.RemoveAll(e => e.TimestampMs < cutoff);
                    state.Entries.Add((destinationUhid, timestampMs));

                    // Count unique destinations in the current window.
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var entry in state.Entries)
                        unique.Add(entry.Destination);

                    if (unique.Count > _scatterThreshold)
                    {
                        var fireAndForget = _reputation.RecordRreqFloodAttemptAsync(sourceUhid);
                        _ = fireAndForget; // suppress CS4014
                        // Evict all entries to avoid re-triggering every subsequent packet.
                        state.Entries.Clear();
                    }
                }
                return state;
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static string ClampedPrefix(string geohash, int length)
        => geohash.Length <= length ? geohash : geohash[..length];

    // ── Inner state types ─────────────────────────────────────────────────────────

    private sealed class VolumeState
    {
        public long   WindowStartMs  { get; set; }
        public int    WindowCount    { get; set; }
        public double EwmaBaseline   { get; set; }
    }

    private sealed class ScatterState
    {
        // (destinationUhid, timestampMs) — locked externally before access.
        public List<(string Destination, long TimestampMs)> Entries { get; } = new();
    }
}
