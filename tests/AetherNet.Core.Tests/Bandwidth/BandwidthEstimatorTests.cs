// SPDX-License-Identifier: MIT

using AetherNet.Bandwidth;
using AetherNet.Transport.Bandwidth;
using Xunit;

namespace AetherNet.Core.Tests.Bandwidth;

/// <summary>
/// Unit tests for BandwidthEstimator (BBRv3 reference implementation).
/// </summary>
public class BandwidthEstimatorTests
{
    private const long MaxBps = 2_000_000L; // 2 Mbps (BLE)

    private static BandwidthEstimator NewEstimator(string name = "BLE") =>
        new(name, MaxBps);

    private static long NowUs() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void NewEstimator_HasNoneConfidence()
    {
        var e = NewEstimator();
        Assert.Equal(BandwidthConfidence.None, e.Confidence);
    }

    [Fact]
    public void NewEstimator_BtlBwEqualsMaxBandwidth()
    {
        var e = NewEstimator();
        // Optimistic initialisation: starts at theoretical max.
        Assert.Equal(MaxBps, e.BtlBwBps);
    }

    // ── RecordDelivery ────────────────────────────────────────────────────────

    [Fact]
    public void RecordDelivery_SingleSample_ConfidenceLow()
    {
        var e = NewEstimator();
        var t0 = NowUs();
        e.RecordDelivery(1024, t0, t0 + 10_000); // 1 KB in 10 ms
        Assert.Equal(BandwidthConfidence.Low, e.Confidence);
    }

    [Fact]
    public void RecordDelivery_20Rounds_ConfidenceHigh()
    {
        var e = NewEstimator();
        var t0 = NowUs();
        for (var i = 0; i < 20; i++)
        {
            var send = t0 + i * 100_000L;
            e.RecordDelivery(1024, send, send + 10_000);
        }
        Assert.Equal(BandwidthConfidence.High, e.Confidence);
    }

    [Fact]
    public void RecordDelivery_HighThroughput_BtlBwUpdates()
    {
        var e = NewEstimator();
        var t0 = NowUs();
        // 10 MB in 80 ms = 1 Gbps delivery rate → well above MaxBps
        // but BtlBwBps should be at least the observed delivery rate
        e.RecordDelivery(1_000_000, t0, t0 + 80_000);
        // Even if capped by PHY, BtlBw should be positive and derived from observation
        Assert.True(e.BtlBwBps > 0);
        Assert.True(e.Confidence >= BandwidthConfidence.Low);
    }

    [Fact]
    public void RecordDelivery_SrttUpdates_WithRfc6298()
    {
        var e = NewEstimator();
        var t0 = NowUs();
        // 10 ms one-way → used as RTT estimate conservatively
        e.RecordDelivery(512, t0, t0 + 10_000);
        Assert.True(e.Srtt > TimeSpan.Zero);
        Assert.True(e.Srtt.TotalMilliseconds <= 30); // 10 ms × some smoothing
    }

    [Fact]
    public void RecordDelivery_ZeroBytesIgnored()
    {
        var e = NewEstimator();
        var t0 = NowUs();
        e.RecordDelivery(0, t0, t0 + 10_000); // must not throw
        Assert.Equal(BandwidthConfidence.None, e.Confidence); // no change
    }

    [Fact]
    public void RecordDelivery_NegativeElapsed_Ignored()
    {
        var e = NewEstimator();
        var t0 = NowUs();
        e.RecordDelivery(512, t0, t0 - 1_000); // deliver before send → ignored
        Assert.Equal(BandwidthConfidence.None, e.Confidence);
    }

    // ── RecordLoss ────────────────────────────────────────────────────────────

    [Fact]
    public void RecordLoss_IncreasesLossRate()
    {
        var e = NewEstimator();
        Assert.Equal(0.0, e.LossRate);
        e.RecordLoss(512);
        Assert.True(e.LossRate > 0.0);
    }

    [Fact]
    public void RecordLoss_DecreasesAvailableBps()
    {
        var e = NewEstimator();
        var t0 = NowUs();
        e.RecordDelivery(1024, t0, t0 + 10_000);
        var availBefore = e.AvailableBps;
        e.RecordLoss(512);
        Assert.True(e.AvailableBps <= availBefore);
    }

    // ── RecordProbeResult ─────────────────────────────────────────────────────

    [Fact]
    public void RecordProbeResult_ValidAck_UpdatesEstimates()
    {
        var e = NewEstimator();
        var now = NowUs();
        var ack = new BandwidthProbeAck(
            Sequence: 1,
            SenderSendUs: now,
            ReceiverReceiveUs: now + 10_000,
            ReceiverSendUs: now + 10_100,
            SenderReceiveUs: now + 20_200,
            ProbeBytes: 64);

        e.RecordProbeResult(ack, now + 20_200);

        Assert.True(e.Srtt.TotalMilliseconds > 0);
        Assert.Equal(BandwidthConfidence.Low, e.Confidence);
    }

    [Fact]
    public void RecordProbeResult_NegativeRtt_Ignored()
    {
        var e = NewEstimator();
        var now = NowUs();
        // SenderReceiveUs < SenderSendUs → negative RTT
        var ack = new BandwidthProbeAck(1, now, now + 5_000, now + 5_100, now - 1_000, 64);
        e.RecordProbeResult(ack, now - 1_000); // must not throw
        Assert.Equal(BandwidthConfidence.None, e.Confidence); // nothing recorded
    }

    // ── WarmFromGossip ────────────────────────────────────────────────────────

    [Fact]
    public void WarmFromGossip_SeedsBtlBwWhenConfidenceIsNone()
    {
        var e = NewEstimator();
        e.WarmFromGossip(500_000L, TimeSpan.FromMilliseconds(15), BandwidthConfidence.Medium);
        Assert.Equal(BandwidthConfidence.Low, e.Confidence); // gossip = Low, not Medium
        Assert.True(e.Srtt.TotalMilliseconds > 0);
    }

    [Fact]
    public void WarmFromGossip_NeverDowngradesExistingEstimate()
    {
        var e = NewEstimator();
        var t0 = NowUs();
        // Build a Medium confidence estimate first.
        for (var i = 0; i < 10; i++)
            e.RecordDelivery(1024, t0 + i * 50_000L, t0 + i * 50_000L + 8_000);

        var btlBwBefore = e.BtlBwBps;
        var confBefore = e.Confidence;

        // Gossip with a much lower estimate — should be ignored.
        e.WarmFromGossip(10L, TimeSpan.FromMilliseconds(1000), BandwidthConfidence.High);

        Assert.Equal(confBefore, e.Confidence);
        Assert.Equal(btlBwBefore, e.BtlBwBps);
    }

    // ── ApplyPhyHint ──────────────────────────────────────────────────────────

    [Fact]
    public void ApplyPhyHint_StrongSignal_NoEffectiveCap()
    {
        var e = NewEstimator();
        e.ApplyPhyHint(-40); // -40 dBm = excellent → 600 Mbps cap
        var s = e.CurrentSample;
        // Cap (600 Mbps) >> MaxBps (2 Mbps) → no capping
        Assert.Equal(s.BtlBwBps, s.EffectiveBps);
    }

    [Fact]
    public void ApplyPhyHint_WeakSignal_CapsEstimate()
    {
        var e = NewEstimator();
        // Feed a very high delivery rate to make BtlBw large.
        var t0 = NowUs();
        for (var i = 0; i < 5; i++)
            e.RecordDelivery(100_000, t0 + i * 5_000L, t0 + i * 5_000L + 100);
        var btlBefore = e.BtlBwBps;

        // Apply very weak signal → 40 kbps cap
        e.ApplyPhyHint(-100);
        var s = e.CurrentSample;

        Assert.True(s.EffectiveBps < btlBefore);
        Assert.Equal(40_000L, s.EffectiveBps);
    }

    // ── BandwidthSample derived properties ────────────────────────────────────

    [Fact]
    public void BandwidthSample_Rto_WithinRfc6298Bounds()
    {
        var sample = new BandwidthSample(
            "BLE", 1_000_000, 900_000, 10_000,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(100), 0.0, 0L, BandwidthConfidence.High, DateTimeOffset.UtcNow);

        // RTO = SRTT + max(1, 4×RTTVAR) = 100 + max(1, 120) = 220 ms (above 200 ms floor → no clamping)
        Assert.Equal(220.0, sample.Rto.TotalMilliseconds, precision: 1);
    }

    [Fact]
    public void BandwidthSample_Rto_ClampedToMin200Ms()
    {
        var sample = new BandwidthSample(
            "NearLink", 10_000_000, 9_000_000, 5_000,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(0),
            TimeSpan.FromMilliseconds(1), 0.0, 0L, BandwidthConfidence.High, DateTimeOffset.UtcNow);

        // SRTT = 1 ms, RTTVAR = 0 → raw = 1 + max(1, 0) = 2 ms → clamped to 200 ms floor
        Assert.Equal(200.0, sample.Rto.TotalMilliseconds, precision: 1);
    }

    // ── SampleImproved event ──────────────────────────────────────────────────

    [Fact]
    public void SampleImproved_FiresOnConfidenceAdvance()
    {
        var e = NewEstimator();
        var gate = new ManualResetEventSlim(false);
        BandwidthSample? fired = null;
        e.SampleImproved += (_, s) => { fired = s; gate.Set(); };

        var t0 = NowUs();
        // First delivery: None → Low confidence → fires SampleImproved.
        e.RecordDelivery(1024, t0, t0 + 10_000);

        // Wait up to 2 s for the ThreadPool work item to execute.
        Assert.True(gate.Wait(TimeSpan.FromSeconds(30)), "SampleImproved did not fire within 30 s");
        Assert.NotNull(fired);
    }

    // ── BandwidthProbeAck ────────────────────────────────────────────────────

    [Fact]
    public void BandwidthProbeAck_Rtt_IsClockSyncFree()
    {
        // SenderSend=100, ReceiverReceive=150, ReceiverSend=160, SenderReceive=220
        // RTT = (220-100) - (160-150) = 120 - 10 = 110 µs
        var ack = new BandwidthProbeAck(1, 100, 150, 160, 220, 64);
        Assert.Equal(TimeSpan.FromMicroseconds(110), ack.Rtt);
    }

    [Fact]
    public void BandwidthProbeAck_ForwardOwd_UsesClockDifference()
    {
        var ack = new BandwidthProbeAck(1, 1000, 1050, 1060, 1120, 64);
        Assert.Equal(TimeSpan.FromMicroseconds(50), ack.ForwardOwd);
    }
}
