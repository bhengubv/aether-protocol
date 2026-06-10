// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Bandwidth;

namespace AetherNet.Transport.Bandwidth;

/// <summary>
/// Production implementation of <see cref="INodeActivityMonitor"/>.
///
/// <para>
/// Runs a background timer loop at <see cref="SampleIntervalMs"/> milliseconds.
/// Each tick computes ingress/egress rates from atomic byte counters, reads
/// per-transport estimates from registered <see cref="IBandwidthEstimator"/>s,
/// and publishes a <see cref="NodeActivitySnapshot"/>.
/// </para>
///
/// <para>Rate computation: byte deltas are divided by the elapsed wall-clock interval.
/// No sliding window is used to keep the implementation allocation-free on the hot path;
/// the sample interval itself acts as the averaging window.</para>
/// </summary>
public sealed class NodeActivityMonitor : INodeActivityMonitor, IDisposable
{
    // ── Configuration ────────────────────────────────────────────────────────

    private volatile int _sampleIntervalMs = 500;
    private volatile int _idleThresholdSeconds = 5;

    // ── Registered transports ────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, (IBandwidthEstimator Estimator, TransportTraffic Traffic)>
        _transports = new(StringComparer.OrdinalIgnoreCase);

    // ── Timer ────────────────────────────────────────────────────────────────

    private Timer? _timer;
    private long _lastTickMs;

    // ── Snapshot ─────────────────────────────────────────────────────────────

    private volatile NodeActivitySnapshot _current = OfflineSnapshot();

    // ── Reactive stream (no System.Reactive dependency) ─────────────────────

    private readonly MeshSubject<NodeActivitySnapshot> _subject = new();

    // ── Constructor / lifecycle ──────────────────────────────────────────────

    public NodeActivityMonitor() { }

    /// <summary>Start the background sampling loop.</summary>
    public void Start()
    {
        _lastTickMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _timer = new Timer(OnTick, null, _sampleIntervalMs, _sampleIntervalMs);
    }

    // ── INodeActivityMonitor ─────────────────────────────────────────────────

    public NodeActivitySnapshot Current => _current;

    public event EventHandler<NodeActivitySnapshot>? SnapshotChanged;

    public IObservable<NodeActivitySnapshot> Activity => _subject;

    public int SampleIntervalMs
    {
        get => _sampleIntervalMs;
        set
        {
            _sampleIntervalMs = Math.Clamp(value, 100, 60_000);
            _timer?.Change(_sampleIntervalMs, _sampleIntervalMs);
        }
    }

    public int IdleThresholdSeconds
    {
        get => _idleThresholdSeconds;
        set => _idleThresholdSeconds = Math.Clamp(value, 1, 300);
    }

    // ── Transport registration ────────────────────────────────────────────────

    /// <summary>Register a transport's estimator so its activity is included in snapshots.</summary>
    public void Register(IBandwidthEstimator estimator)
    {
        _transports[estimator.TransportName] = (estimator, new TransportTraffic());
    }

    /// <summary>Record inbound bytes on a transport. Call from transport receive path.</summary>
    public void RecordIngress(string transportName, int bytes)
    {
        if (_transports.TryGetValue(transportName, out var entry))
            Interlocked.Add(ref entry.Traffic.IngressBytes, bytes);
    }

    /// <summary>Record outbound bytes on a transport. Call from transport send path.</summary>
    public void RecordEgress(string transportName, int bytes)
    {
        if (_transports.TryGetValue(transportName, out var entry))
        {
            Interlocked.Add(ref entry.Traffic.EgressBytes, bytes);
            entry.Traffic.LastEgressMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    // ── Timer callback ────────────────────────────────────────────────────────

    private void OnTick(object? _)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var elapsedSec = Math.Max(0.001, (nowMs - _lastTickMs) / 1000.0);
        _lastTickMs = nowMs;

        var transportSnapshots = new List<TransportActivitySnapshot>(_transports.Count);
        long totalIngress = 0, totalEgress = 0;
        int activePeers = 0, activeTransports = 0;
        var idleThresholdMs = _idleThresholdSeconds * 1000L;

        foreach (var (name, (estimator, traffic)) in _transports)
        {
            // Atomically sample and reset byte counters.
            var ingressDelta = Interlocked.Exchange(ref traffic.IngressBytes, 0L);
            var egressDelta  = Interlocked.Exchange(ref traffic.EgressBytes,  0L);

            var ingressBps = (long)(ingressDelta * 8.0 / elapsedSec);
            var egressBps  = (long)(egressDelta  * 8.0 / elapsedSec);

            var sample = estimator.CurrentSample;
            var utilFraction = sample.BtlBwBps > 0
                ? Math.Clamp(egressBps / (double)sample.BtlBwBps, 0.0, 1.0)
                : 0.0;

            var isRecent = (nowMs - traffic.LastEgressMs) < idleThresholdMs;
            var state = ComputeTransportState(egressBps, ingressBps, sample, isRecent);

            if (state is not NodeActivityState.Offline and not NodeActivityState.Idle)
                activeTransports++;

            totalIngress += ingressBps;
            totalEgress += egressBps;

            transportSnapshots.Add(new TransportActivitySnapshot(
                name,
                IsAvailable: true,
                ingressBps,
                egressBps,
                sample.Srtt,
                sample.BtlBwBps,
                utilFraction,
                state,
                sample.Confidence));
        }

        var nodeState = ComputeNodeState(transportSnapshots);
        var primary = transportSnapshots.Count > 0
            ? transportSnapshots.MaxBy(t => t.EgressBps)?.TransportName
            : null;

        var snapshot = new NodeActivitySnapshot(
            nodeState,
            totalIngress,
            totalEgress,
            activePeers,
            activeTransports,
            transportSnapshots,
            primary,
            DateTimeOffset.UtcNow);

        var prev = _current;
        _current = snapshot;

        // Emit on the observable stream unconditionally (heartbeat).
        try { _subject.OnNext(snapshot); } catch { /* observer errors must not kill the timer */ }

        // Fire SnapshotChanged only when something meaningful changed.
        if (snapshot.State != prev.State ||
            Math.Abs(snapshot.TotalBps - prev.TotalBps) > 1_000 ||
            snapshot.ActiveTransports != prev.ActiveTransports)
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }
    }

    // ── State computation ────────────────────────────────────────────────────

    private static NodeActivityState ComputeTransportState(
        long egressBps, long ingressBps,
        BandwidthSample sample, bool isRecent)
    {
        if (!isRecent && egressBps == 0 && ingressBps == 0) return NodeActivityState.Idle;
        if (egressBps == 0 && ingressBps == 0) return NodeActivityState.Idle;

        if (sample.LossRate > 0.05) return NodeActivityState.Degraded;

        var util = sample.BtlBwBps > 0
            ? egressBps / (double)sample.BtlBwBps
            : 0.0;

        return util >= 0.5 ? NodeActivityState.Busy : NodeActivityState.Active;
    }

    private static NodeActivityState ComputeNodeState(IReadOnlyList<TransportActivitySnapshot> transports)
    {
        if (transports.Count == 0) return NodeActivityState.Offline;

        if (transports.Any(t => t.State == NodeActivityState.Degraded)) return NodeActivityState.Degraded;
        if (transports.Any(t => t.State == NodeActivityState.Busy))     return NodeActivityState.Busy;
        if (transports.Any(t => t.State == NodeActivityState.Active))   return NodeActivityState.Active;
        if (transports.All(t => t.State == NodeActivityState.Offline))  return NodeActivityState.Offline;

        return NodeActivityState.Idle;
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private static NodeActivitySnapshot OfflineSnapshot() =>
        new(NodeActivityState.Offline, 0L, 0L, 0, 0,
            Array.Empty<TransportActivitySnapshot>(), null, DateTimeOffset.UtcNow);

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        _timer?.Dispose();
        _subject.OnCompleted();
    }

    // ── Inner types ──────────────────────────────────────────────────────────

    /// <summary>Mutable traffic accumulators for one transport (reset each tick).</summary>
    private sealed class TransportTraffic
    {
        public long IngressBytes;
        public long EgressBytes;
        public long LastEgressMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}

/// <summary>
/// Minimal hot-observable subject that implements <see cref="IObservable{T}"/>
/// without any System.Reactive dependency.
/// Thread-safe. Observers receive <c>OnNext</c> synchronously on the calling thread.
/// </summary>
/// <summary>
/// Minimal hot-observable that implements <see cref="IObservable{T}"/>
/// without any System.Reactive dependency. Thread-safe.
/// </summary>
internal sealed class MeshSubject<T> : IObservable<T>
{
    private readonly List<IObserver<T>> _observers = new();
    private readonly object _lock = new();
    private bool _completed;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_lock)
        {
            if (_completed) { observer.OnCompleted(); return NoopDisposable.Instance; }
            _observers.Add(observer);
        }
        return new ActionDisposable(() => { lock (_lock) _observers.Remove(observer); });
    }

    public void OnNext(T value)
    {
        IObserver<T>[] snapshot;
        lock (_lock)
        {
            if (_completed) return;
            snapshot = _observers.ToArray();
        }
        foreach (var o in snapshot)
            try { o.OnNext(value); } catch { /* observer errors must not crash the timer loop */ }
    }

    public void OnCompleted()
    {
        IObserver<T>[] snapshot;
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
            snapshot = _observers.ToArray();
            _observers.Clear();
        }
        foreach (var o in snapshot)
            try { o.OnCompleted(); } catch { }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
