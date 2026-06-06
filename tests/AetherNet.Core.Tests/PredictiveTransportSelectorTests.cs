// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Transport.Abstractions;
using AetherNet.Transport.Models;
using AetherNet.Transport.Services;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for PredictiveTransportSelector — Kalman RTT filter and scoring.
/// </summary>
public sealed class PredictiveTransportSelectorTests
{
    // ── FakeTransport — minimal ITransportService stub ────────────────────────

    private sealed class FakeTransport : ITransportService
    {
        public string Name            { get; }
        public bool   IsAvailable     { get; }
        public long   MaxBandwidthBps { get; }
        public int    MaxRangeMeters  => 100;
        public int    PowerCostRelative { get; }
        public int    MaxConcurrentPeers => 10;
        public PerTransportMetrics? Metrics { get; } = new();

        public FakeTransport(
            string name,
            long   bandwidthBps  = 500_000L,
            int    powerCost     = 1,
            bool   available     = true)
        {
            Name              = name;
            MaxBandwidthBps   = bandwidthBps;
            PowerCostRelative = powerCost;
            IsAvailable       = available;
        }

        public Task<bool> SendAsync(string _, byte[] __, CancellationToken ___)
            => Task.FromResult(true);
        public Task<bool> SendStreamAsync(string _, Stream __, CancellationToken ___)
            => Task.FromResult(true);
        public bool IsConnected(string _) => false;
#pragma warning disable CS0067
        public event Action<string, byte[]>? DataReceived;
#pragma warning restore CS0067
    }

    // ── Kalman filter (indirect) ──────────────────────────────────────────────

    [Fact]
    public void Kalman_ConvergesOnSteadyState()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        sel.Register(t, 200.0);

        for (int i = 0; i < 50; i++)
            sel.ObserveMetrics(t, rttMs: 100, success: true, bytesTransferred: 1000);

        var state = sel.GetKalmanState(t);
        Assert.NotNull(state);
        Assert.True(Math.Abs(state!.Value.RttMs - 100.0) < 5.0,
            $"Kalman did not converge: rttMs={state.Value.RttMs:F2}, want ~100");
    }

    [Fact]
    public void Kalman_VarianceDecreases_WithObservations()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        sel.Register(t, 200.0);
        double initialVar = sel.GetKalmanState(t)!.Value.Variance;

        for (int i = 0; i < 10; i++)
            sel.ObserveMetrics(t, rttMs: 200, success: true, bytesTransferred: 1000);

        double afterVar = sel.GetKalmanState(t)!.Value.Variance;
        Assert.True(afterVar < initialVar,
            $"posterior variance {afterVar:F4} should be < initial {initialVar:F4}");
    }

    [Fact]
    public void Kalman_DetectsPositiveDrift_ForRisingRtt()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        sel.Register(t, 100.0);

        for (int i = 0; i < 10; i++)
            sel.ObserveMetrics(t, rttMs: 100 + (i + 1) * 15L, success: true, bytesTransferred: 1000);

        var state = sel.GetKalmanState(t);
        Assert.NotNull(state);
        Assert.True(state!.Value.DriftMs > 0.0,
            $"drift {state.Value.DriftMs:F4} should be positive for rising RTT");
    }

    // ── PredictiveTransportSelector lifecycle ─────────────────────────────────

    [Fact]
    public void RegisterAndRank_FastTransportFirst()
    {
        var sel  = new PredictiveTransportSelector();
        var fast = new FakeTransport("fast", bandwidthBps: 1_000_000L, powerCost: 1);
        var slow = new FakeTransport("slow", bandwidthBps: 10_000L,    powerCost: 10);
        sel.Register(fast, 50.0);
        sel.Register(slow, 150.0);

        for (int i = 0; i < 5; i++)
            sel.ObserveMetrics(fast, rttMs: 50, success: true, bytesTransferred: 1000);

        var ranked = sel.Rank(payloadBytes: 100);
        Assert.Equal(2, ranked.Count);            // two transports registered
        Assert.Equal("fast", ranked[0].Transport.Name);
    }

    [Fact]
    public void UnavailableTransport_ExcludedFromRank()
    {
        var sel     = new PredictiveTransportSelector();
        var avail   = new FakeTransport("avail",   available: true);
        var unavail = new FakeTransport("unavail", available: false);
        sel.Register(avail,   100.0);
        sel.Register(unavail, 100.0);

        var ranked = sel.Rank();
        var only   = Assert.Single(ranked);
        Assert.Equal("avail", only.Transport.Name);
    }

    [Fact]
    public void Unregister_RemovesTransport()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        sel.Register(t, 100.0);
        sel.Unregister(t);
        Assert.Empty(sel.Rank());
    }

    [Fact]
    public void SelectBest_ReturnsNull_WhenEmpty()
    {
        var sel = new PredictiveTransportSelector();
        Assert.Null(sel.SelectBest());
    }

    [Fact]
    public void DuplicateRegister_IsNoOp()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        sel.Register(t, 100.0);
        sel.Register(t, 200.0); // duplicate — should be ignored
        Assert.Single(sel.Rank());
    }

    [Fact]
    public void GetKalmanState_InitialValues_AreCorrect()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        sel.Register(t, 123.0);

        var state = sel.GetKalmanState(t);
        Assert.NotNull(state);
        Assert.Equal(123.0, state!.Value.RttMs,   tolerance: 1e-9);
        Assert.Equal(0.0,   state!.Value.DriftMs, tolerance: 1e-9);
        Assert.True(state!.Value.Variance > 0.0);
    }

    [Fact]
    public void GetKalmanState_UnregisteredTransport_ReturnsNull()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        Assert.Null(sel.GetKalmanState(t));
    }

    [Fact]
    public void Rank_ReturnsPositiveScore()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        sel.Register(t, 100.0);

        var ranked = sel.Rank();
        var only   = Assert.Single(ranked);
        Assert.True(only.Score > 0.0);
    }

    [Fact]
    public void Score_ImprovesAfterGoodObservations()
    {
        var sel = new PredictiveTransportSelector();
        var t   = new FakeTransport("t");
        sel.Register(t, 200.0);
        double scoreBefore = sel.Rank()[0].Score;

        for (int i = 0; i < 10; i++)
            sel.ObserveMetrics(t, rttMs: 20, success: true, bytesTransferred: 5000);

        double scoreAfter = sel.Rank()[0].Score;
        Assert.True(scoreAfter > scoreBefore,
            $"score should improve after good observations (before={scoreBefore:F4}, after={scoreAfter:F4})");
    }
}
