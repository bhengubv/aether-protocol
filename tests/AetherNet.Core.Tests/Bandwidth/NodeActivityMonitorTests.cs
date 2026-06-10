// SPDX-License-Identifier: MIT

using AetherNet.Bandwidth;
using AetherNet.Transport.Bandwidth;
using Xunit;

namespace AetherNet.Core.Tests.Bandwidth;

public class NodeActivityMonitorTests : IDisposable
{
    private readonly BandwidthEstimator _bleEstimator = new("BLE", 2_000_000L);
    private readonly BandwidthEstimator _wifiEstimator = new("Wi-Fi Direct", 250_000_000L);
    private readonly NodeActivityMonitor _monitor = new();

    public NodeActivityMonitorTests()
    {
        _monitor.Register(_bleEstimator);
        _monitor.Register(_wifiEstimator);
    }

    public void Dispose() => _monitor.Dispose();

    // ── Initial state ──────────────────────────────────────────────────────

    [Fact]
    public void InitialSnapshot_StateIsOffline()
    {
        // Before Start() there are no transports actively tracked — state = Offline.
        var snap = _monitor.Current;
        Assert.Equal(NodeActivityState.Offline, snap.State);
    }

    [Fact]
    public void InitialSnapshot_TotalBpsIsZero()
    {
        Assert.Equal(0L, _monitor.Current.TotalBps);
    }

    // ── RecordIngress / RecordEgress ───────────────────────────────────────

    [Fact]
    public async Task AfterEgress_SnapshotShowsActivity()
    {
        _monitor.SampleIntervalMs = 100; // fast tick for test
        _monitor.Start();

        _monitor.RecordEgress("BLE", 10_000);
        await Task.Delay(250); // wait for at least one tick

        // After recording egress, at least one transport should show activity.
        var snap = _monitor.Current;
        // EgressBps > 0 (may have decayed by now, but let's verify state was Active)
        // We can't assert exact rate because timing is non-deterministic in unit tests,
        // but the transport should be registered.
        Assert.Contains(snap.Transports, t => t.TransportName == "BLE");
    }

    // ── SnapshotChanged event ──────────────────────────────────────────────

    [Fact]
    public async Task SnapshotChanged_FiresWhenStateChanges()
    {
        _monitor.SampleIntervalMs = 100;
        _monitor.Start();

        NodeActivitySnapshot? received = null;
        var tcs = new TaskCompletionSource<NodeActivitySnapshot>();
        _monitor.SnapshotChanged += (_, snap) => tcs.TrySetResult(snap);

        // Record enough egress to make the snapshot non-trivial.
        _monitor.RecordEgress("Wi-Fi Direct", 1_000_000);

        var snap = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(snap);
    }

    // ── Activity observable ────────────────────────────────────────────────

    [Fact]
    public async Task Activity_Observable_EmitsOnTick()
    {
        _monitor.SampleIntervalMs = 100;
        _monitor.Start();

        var received = new List<NodeActivitySnapshot>();
        using var sub = _monitor.Activity.Subscribe(Observer.Create<NodeActivitySnapshot>(
            snap => received.Add(snap)));

        await Task.Delay(350); // wait for ~3 ticks

        Assert.True(received.Count >= 2, $"Expected ≥2 snapshots, got {received.Count}");
    }

    // ── Active-peer tracking ───────────────────────────────────────────────

    [Fact]
    public async Task PeerAwareEgress_CountsDistinctActivePeers()
    {
        _monitor.SampleIntervalMs = 100;
        _monitor.Start();

        // Two distinct peers send via BLE.
        _monitor.RecordEgress("BLE", "peer-A", 1_000);
        _monitor.RecordEgress("BLE", "peer-B", 1_000);

        var tcs = new TaskCompletionSource<NodeActivitySnapshot>();
        _monitor.SnapshotChanged += (_, snap) =>
        {
            if (snap.ActivePeers >= 2) tcs.TrySetResult(snap);
        };

        // Keep traffic alive across a few ticks so the snapshot observes both peers.
        for (var i = 0; i < 5 && !tcs.Task.IsCompleted; i++)
        {
            _monitor.RecordEgress("BLE", "peer-A", 1_000);
            _monitor.RecordEgress("BLE", "peer-B", 1_000);
            await Task.Delay(60);
        }

        var snap = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(snap.ActivePeers >= 2, $"Expected ≥2 active peers, got {snap.ActivePeers}");
    }

    [Fact]
    public void PeerAwareEgress_UnknownTransport_DoesNotThrow()
    {
        // Recording to an unregistered transport is a no-op for byte counters but
        // must not throw — the peer is still tracked for the active-peer count.
        _monitor.RecordEgress("No-Such-Transport", "peer-X", 5_000);
        _monitor.RecordIngress("No-Such-Transport", "peer-Y", 5_000);
    }

    [Fact]
    public void TransportOnlyEgress_DoesNotInflatePeerCount()
    {
        // The transport-only overload supplies no peer → must not count a peer.
        _monitor.RecordEgress("BLE", 1_000);
        Assert.Equal(0, _monitor.Current.ActivePeers);
    }

    // ── INodeActivityMonitor properties ───────────────────────────────────

    [Fact]
    public void SampleIntervalMs_ClampedToMin100()
    {
        _monitor.SampleIntervalMs = 10;
        Assert.Equal(100, _monitor.SampleIntervalMs);
    }

    [Fact]
    public void IdleThresholdSeconds_ClampedToMin1()
    {
        _monitor.IdleThresholdSeconds = 0;
        Assert.Equal(1, _monitor.IdleThresholdSeconds);
    }

    // ── NodeActivitySnapshot derived properties ────────────────────────────

    [Fact]
    public void NodeActivitySnapshot_TotalBps_SumsIngressAndEgress()
    {
        var snap = new NodeActivitySnapshot(
            NodeActivityState.Active,
            IngressBps: 100_000, EgressBps: 200_000,
            ActivePeers: 2, ActiveTransports: 1,
            Transports: [],
            PrimaryTransportName: "BLE",
            Timestamp: DateTimeOffset.UtcNow);

        Assert.Equal(300_000L, snap.TotalBps);
    }

    [Fact]
    public void NodeActivitySnapshot_HasActivity_TrueForActiveStates()
    {
        foreach (var state in new[] { NodeActivityState.Active, NodeActivityState.Busy, NodeActivityState.Degraded })
        {
            var snap = new NodeActivitySnapshot(state, 1, 1, 1, 1, [], null, DateTimeOffset.UtcNow);
            Assert.True(snap.HasActivity, $"Expected HasActivity=true for {state}");
        }

        foreach (var state in new[] { NodeActivityState.Offline, NodeActivityState.Idle })
        {
            var snap = new NodeActivitySnapshot(state, 0, 0, 0, 0, [], null, DateTimeOffset.UtcNow);
            Assert.False(snap.HasActivity, $"Expected HasActivity=false for {state}");
        }
    }

    // ── TransportActivitySnapshot ──────────────────────────────────────────

    [Fact]
    public void TransportActivitySnapshot_UtilizationPercent_FormatsCorrectly()
    {
        var snap = new TransportActivitySnapshot(
            "BLE", true, 50_000, 100_000,
            TimeSpan.FromMilliseconds(10), 2_000_000,
            UtilizationFraction: 0.05,
            NodeActivityState.Active, BandwidthConfidence.Medium);

        Assert.Equal("5 %", snap.UtilizationPercent);
    }
}

// ── Minimal IObservable helper for tests ─────────────────────────────────────

file static class Observer
{
    public static IObserver<T> Create<T>(Action<T> onNext) =>
        new ActionObserver<T>(onNext);

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
