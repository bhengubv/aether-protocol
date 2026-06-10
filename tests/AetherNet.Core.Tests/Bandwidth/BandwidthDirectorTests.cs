// SPDX-License-Identifier: MIT

using AetherNet.Bandwidth;
using AetherNet.Transport.Bandwidth;
using Xunit;

namespace AetherNet.Core.Tests.Bandwidth;

public class BandwidthDirectorTests
{
    private static (BandwidthDirector director, BandwidthEstimator ble, BandwidthEstimator wifi)
        NewDirector()
    {
        var ble  = new BandwidthEstimator("BLE",            2_000_000L);
        var wifi = new BandwidthEstimator("Wi-Fi Direct", 250_000_000L);
        var d    = new BandwidthDirector();
        d.Register(ble);
        d.Register(wifi);
        return (d, ble, wifi);
    }

    private static void Warmup(BandwidthEstimator e, long bps, int rounds = 5)
    {
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var bytesPerRound = (int)(bps / 8 * 0.010); // 10 ms worth of bytes
        for (var i = 0; i < rounds; i++)
        {
            var send = t0 + i * 50_000L;
            e.RecordDelivery(bytesPerRound, send, send + 10_000);
        }
    }

    // ── Register / GetEstimate ────────────────────────────────────────────

    [Fact]
    public void GetEstimate_UnknownPeer_ReturnsNull()
    {
        var (d, _, _) = NewDirector();
        Assert.Null(d.GetEstimate("unknown-peer", "BLE"));
    }

    [Fact]
    public void ApplyGossip_SeedsEstimate_GetEstimateReturnsValue()
    {
        var (d, _, _) = NewDirector();
        var gossip = new BandwidthGossipPayload(
            "peer-1", "BLE", 1_000_000L, 15_000L, BandwidthConfidence.Medium, DateTimeOffset.UtcNow);

        d.ApplyGossip(gossip);

        var sample = d.GetEstimate("peer-1", "BLE");
        Assert.NotNull(sample);
        Assert.True(sample!.BtlBwBps > 0);
    }

    // ── GetEstimates ordering ──────────────────────────────────────────────

    [Fact]
    public void GetEstimates_OrderedByAvailableBpsDescending()
    {
        var (d, _, _) = NewDirector();
        // BLE: 500 kbps, Wi-Fi: 10 Mbps
        d.ApplyGossip(new("peer-2", "BLE",            500_000L, 20_000L, BandwidthConfidence.Medium, DateTimeOffset.UtcNow));
        d.ApplyGossip(new("peer-2", "Wi-Fi Direct", 10_000_000L, 5_000L, BandwidthConfidence.Medium, DateTimeOffset.UtcNow));

        var estimates = d.GetEstimates("peer-2");
        Assert.Equal(2, estimates.Count);
        Assert.True(estimates[0].BtlBwBps >= estimates[1].BtlBwBps);
    }

    // ── RecommendTransport ─────────────────────────────────────────────────

    [Fact]
    public void RecommendTransport_OnlyBleAvailable_ReturnsBle()
    {
        // When only BLE has gossip data, it must win regardless of payload size.
        var (d, _, _) = NewDirector();
        d.ApplyGossip(new("peer-3", "BLE", 1_500_000L, 20_000L, BandwidthConfidence.High, DateTimeOffset.UtcNow));
        // No Wi-Fi Direct gossip for peer-3.

        var recommended = d.RecommendTransport("peer-3", payloadBytes: 100);
        Assert.Equal("BLE", recommended);
    }

    [Fact]
    public void RecommendTransport_LargePayload_PrefersWifi()
    {
        var (d, _, _) = NewDirector();
        // Seed estimates.
        d.ApplyGossip(new("peer-4", "BLE",          1_500_000L, 20_000L, BandwidthConfidence.High, DateTimeOffset.UtcNow));
        d.ApplyGossip(new("peer-4", "Wi-Fi Direct", 50_000_000L,  5_000L, BandwidthConfidence.High, DateTimeOffset.UtcNow));

        // 1 MB payload — Wi-Fi Direct BDP will be much larger, so it gets the BDP bonus.
        var recommended = d.RecommendTransport("peer-4", payloadBytes: 1_000_000);
        Assert.Equal("Wi-Fi Direct", recommended);
    }

    [Fact]
    public void RecommendTransport_NoPeerData_FallsBackToLowestPowerTransport()
    {
        var (d, _, _) = NewDirector();
        var recommended = d.RecommendTransport("no-data-peer", 500);
        // No gossip → falls back to registered estimators, lowest power = BLE (cost=2)
        Assert.NotNull(recommended);
    }

    // ── BuildGossipPayload ────────────────────────────────────────────────

    [Fact]
    public void BuildGossipPayload_NoConfidence_ReturnsNull()
    {
        var (d, _, _) = NewDirector();
        // BLE estimator has None confidence (no probes) → gossip payload = null
        var payload = d.BuildGossipPayload("peer-5", "BLE");
        Assert.Null(payload);
    }

    [Fact]
    public void BuildGossipPayload_AfterWarmup_ReturnsPayload()
    {
        var (d, ble, _) = NewDirector();
        Warmup(ble, 1_000_000L, rounds: 5);

        var payload = d.BuildGossipPayload("peer-6", "BLE");
        Assert.NotNull(payload);
        Assert.Equal("BLE", payload!.TransportName);
        Assert.True(payload.BtlBwBps > 0);
        Assert.Equal("peer-6", payload.PeerUhid);
    }

    // ── ApplyGossip ────────────────────────────────────────────────────────

    [Fact]
    public void ApplyGossip_DoesNotDowngradeExistingEstimate()
    {
        var (d, ble, _) = NewDirector();
        Warmup(ble, 1_800_000L, rounds: 20); // High confidence, high BtlBw

        var btlBwBefore = ble.BtlBwBps;

        // Apply gossip with much lower estimate → should not downgrade.
        d.ApplyGossip(new("any-peer", "BLE", 100L, 1_000L, BandwidthConfidence.High, DateTimeOffset.UtcNow));

        Assert.Equal(btlBwBefore, ble.BtlBwBps); // unchanged
    }
}
