// SPDX-License-Identifier: MIT

using Aether.Reputation;
using Xunit;

namespace Aether.Core.Tests;

/// <summary>
/// Unit tests for <see cref="BehavioralAnomalyDetector"/>.
///
/// Uses <see cref="SpyReputationService"/> to capture signals rather than verifying
/// score values — we only care that the correct signal methods were called.
///
/// Time is injected via synthetic <c>timestampMs</c> arguments so tests don't
/// depend on a real clock.
/// </summary>
public class BehavioralAnomalyDetectorTests
{
    private const string Alice = "alice-uhid";
    private const string Bob   = "bob-uhid";
    private const string Carol = "carol-uhid";

    private static (BehavioralAnomalyDetector Detector, SpyReputationService Spy) NewPair(
        AnomalyDetectorOptions? options = null)
    {
        var spy = new SpyReputationService();
        var detector = new BehavioralAnomalyDetector(spy, options);
        return (detector, spy);
    }

    // ── Volume spike ──────────────────────────────────────────────────────────────

    [Fact]
    public void VolumeSpikeDetected_WhenCountExceedsMultiplierTimesEwma()
    {
        // Use a short window so we can inject synthetic timestamps.
        var opts = new AnomalyDetectorOptions
        {
            VolumeWindowMs        = 1_000,  // 1 s
            VolumeSpikeMultiplier = 3.0,
            EwmaAlpha             = 1.0,    // EWMA = exactly the last window count
        };
        var (detector, spy) = NewPair(opts);

        // Window 1 (t=0..999 ms): 5 packets — this establishes EWMA baseline = 5.
        for (var i = 0; i < 5; i++)
            detector.ObservePacket(Alice, Bob, timestampMs: i * 100);

        // Window 2 (t=1000..1999 ms): 20 packets → 20 > 3 × 5 = 15 → spike.
        for (var i = 0; i < 20; i++)
            detector.ObservePacket(Alice, Bob, timestampMs: 1_000 + i * 10);

        Assert.Contains(Alice, spy.RreqFloodAttempts);
    }

    [Fact]
    public void NoFalseVolumeSpikeForNormalTraffic()
    {
        var opts = new AnomalyDetectorOptions
        {
            VolumeWindowMs        = 1_000,
            VolumeSpikeMultiplier = 5.0,
            EwmaAlpha             = 1.0,
        };
        var (detector, spy) = NewPair(opts);

        // Window 1: 10 packets → EWMA = 10.
        for (var i = 0; i < 10; i++)
            detector.ObservePacket(Alice, Bob, timestampMs: i * 80);

        // Window 2: 12 packets → 12 < 5 × 10 = 50 → no spike.
        for (var i = 0; i < 12; i++)
            detector.ObservePacket(Alice, Bob, timestampMs: 1_000 + i * 80);

        Assert.Empty(spy.RreqFloodAttempts);
    }

    // ── Destination scatter ───────────────────────────────────────────────────────

    [Fact]
    public void DestinationScatterDetected_WhenUniqueDestsExceedThreshold()
    {
        var opts = new AnomalyDetectorOptions
        {
            ScatterWindowMs   = 60_000,
            ScatterThreshold  = 5,  // low threshold for testing
        };
        var (detector, spy) = NewPair(opts);

        // Send to 6 unique destinations within the window.
        for (var i = 0; i < 6; i++)
            detector.ObservePacket(Alice, $"dest-{i}", timestampMs: 0);

        Assert.Contains(Alice, spy.RreqFloodAttempts);
    }

    [Fact]
    public void DestinationScatterNotDetected_WhenRepeatingDestinations()
    {
        var opts = new AnomalyDetectorOptions { ScatterThreshold = 5 };
        var (detector, spy) = NewPair(opts);

        // 100 packets to only 3 unique destinations — not a scatter.
        for (var i = 0; i < 100; i++)
            detector.ObservePacket(Alice, $"dest-{i % 3}", timestampMs: i * 10);

        Assert.Empty(spy.RreqFloodAttempts);
    }

    [Fact]
    public void DestinationScatterWindowed_OldEntriesAreEvicted()
    {
        var opts = new AnomalyDetectorOptions
        {
            ScatterWindowMs  = 1_000,
            ScatterThreshold = 5,
        };
        var (detector, spy) = NewPair(opts);

        // 4 unique dests in the first window — under threshold.
        for (var i = 0; i < 4; i++)
            detector.ObservePacket(Alice, $"old-{i}", timestampMs: 0);

        // 4 new unique dests starting at t=2000 (all old entries now expired).
        // Total unique in the new window = 4, still under threshold.
        for (var i = 0; i < 4; i++)
            detector.ObservePacket(Alice, $"new-{i}", timestampMs: 2_000 + i * 10);

        Assert.Empty(spy.RreqFloodAttempts);
    }

    // ── Geohash mismatch ──────────────────────────────────────────────────────────

    [Fact]
    public void GeohashMismatch_EmitsSigFailureSignal()
    {
        var opts = new AnomalyDetectorOptions
        {
            GeohashPrefixLength = 4,
            GeohashRateLimitMs  = 0, // No rate limit in tests.
        };
        var (detector, spy) = NewPair(opts);

        detector.ObserveGeohashClaim(Alice, claimedGeohash: "abcd1234", observedRoutingGeohash: "wxyz5678");

        Assert.Contains(Alice, spy.SignatureFailures);
    }

    [Fact]
    public void GeohashMatch_EmitsNoSignal()
    {
        var (detector, spy) = NewPair();
        detector.ObserveGeohashClaim(Alice, claimedGeohash: "abcd9999", observedRoutingGeohash: "abcd1111");
        Assert.Empty(spy.SignatureFailures);
    }

    [Fact]
    public void GeohashMismatch_RateLimited_EmitsOnlyOnce()
    {
        var opts = new AnomalyDetectorOptions
        {
            GeohashPrefixLength = 4,
            GeohashRateLimitMs  = int.MaxValue, // Never allow a second signal.
        };
        var (detector, spy) = NewPair(opts);

        // First mismatch should fire.
        detector.ObserveGeohashClaim(Alice, "aaaa", "bbbb");
        // Second mismatch immediately after should be rate-limited.
        detector.ObserveGeohashClaim(Alice, "aaaa", "cccc");

        Assert.Single(spy.SignatureFailures, Alice);
    }

    // ── SPK-sig failure ───────────────────────────────────────────────────────────

    [Fact]
    public void SpkSigFailure_EmitsSigFailureSignal()
    {
        var (detector, spy) = NewPair();
        detector.ObserveSpkSigFailure(Alice);
        Assert.Contains(Alice, spy.SignatureFailures);
    }

    [Fact]
    public void SpkSigFailure_EmitsForEachCall()
    {
        var (detector, spy) = NewPair();
        detector.ObserveSpkSigFailure(Alice);
        detector.ObserveSpkSigFailure(Alice);
        detector.ObserveSpkSigFailure(Alice);
        Assert.Equal(3, spy.SignatureFailures.Count(u => u == Alice));
    }

    // ── No cross-contamination ────────────────────────────────────────────────────

    [Fact]
    public void Signals_DoNotCrossContaminateNodes()
    {
        var opts = new AnomalyDetectorOptions { ScatterThreshold = 2 };
        var (detector, spy) = NewPair(opts);

        // Only Alice exceeds scatter threshold.
        detector.ObservePacket(Alice, "dest-0", timestampMs: 0);
        detector.ObservePacket(Alice, "dest-1", timestampMs: 0);
        detector.ObservePacket(Alice, "dest-2", timestampMs: 0); // triggers

        Assert.Contains(Alice, spy.RreqFloodAttempts);
        Assert.DoesNotContain(Bob, spy.RreqFloodAttempts);
    }

    // ── Spy ───────────────────────────────────────────────────────────────────────

    private sealed class SpyReputationService : INodeReputationService
    {
        public List<string> RreqFloodAttempts   { get; } = new();
        public List<string> ReplayAttempts      { get; } = new();
        public List<string> SignatureFailures   { get; } = new();
        public List<string> CustodyRefusals     { get; } = new();
        public List<string> DeliverySuccesses   { get; } = new();
        public List<string> DeliveryFailures    { get; } = new();

        public Task RecordRreqFloodAttemptAsync(string uhid, CancellationToken ct = default)
        {
            RreqFloodAttempts.Add(uhid);
            return Task.CompletedTask;
        }

        public Task RecordReplayAttemptAsync(string uhid, CancellationToken ct = default)
        {
            ReplayAttempts.Add(uhid);
            return Task.CompletedTask;
        }

        public Task RecordSignatureFailureAsync(string uhid, CancellationToken ct = default)
        {
            SignatureFailures.Add(uhid);
            return Task.CompletedTask;
        }

        public Task RecordCustodyRefusalAsync(string uhid, CancellationToken ct = default)
        {
            CustodyRefusals.Add(uhid);
            return Task.CompletedTask;
        }

        public Task RecordDeliverySuccessAsync(string uhid, int roundTripMs, CancellationToken ct = default)
        {
            DeliverySuccesses.Add(uhid);
            return Task.CompletedTask;
        }

        public Task RecordDeliveryFailureAsync(string uhid, CancellationToken ct = default)
        {
            DeliveryFailures.Add(uhid);
            return Task.CompletedTask;
        }

        public Task<double> GetReputationScoreAsync(string uhid, CancellationToken ct = default)
            => Task.FromResult(1.0);

        public Task<IReadOnlyDictionary<string, double>> GetAllScoresAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());

        public Task ApplyWeightedDeltaAsync(string uhid, double weightedDelta, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
