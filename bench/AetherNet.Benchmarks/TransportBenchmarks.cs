// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Routing;
using AetherNet.Transport.Models;
using BenchmarkDotNet.Attributes;

namespace AetherNet.Benchmarks;

/// <summary>
/// Benchmarks for the transport routing and EWMA metrics hot paths.
///
/// <para><b>Routing throughput</b> — <see cref="InMemoryRouteStore"/> covers the
/// cached-route path (Get) and the RREP-install path (Save) that fire on every
/// outbound packet and every AODV reply respectively.</para>
///
/// <para><b>BLE simulation</b> — <see cref="PerTransportMetrics.RecordSample"/>
/// fires once per sent BLE packet for the EWMA RTT/loss/throughput update.
/// On a BLE mesh with 20 Kbps links and 100ms RTT each packet fires this
/// method. Pinning the baseline ensures BCL lock / interlocked regressions
/// are visible.</para>
///
/// <para><b>Composite score</b> — called by <c>PredictiveTransportSelector</c>
/// each time it must pick the best transport. On a device with 4 active transports
/// this fires 4× per routing decision, so the cost accumulates.</para>
/// </summary>
[MemoryDiagnoser]
public class TransportBenchmarks
{
    // ── Route store setup ──────────────────────────────────────────────────

    private InMemoryRouteStore _store = null!;
    private RouteEntry         _entry = null!;
    private const string       DestUhid = "dest-uhid-0001";

    // ── EWMA metrics setup ─────────────────────────────────────────────────

    private PerTransportMetrics _bleMetrics   = null!;
    private PerTransportMetrics _warmMetrics  = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Route store — pre-seed one entry so Get has a cache hit.
        _store = new InMemoryRouteStore();
        _entry = new RouteEntry
        {
            DestinationUhid = DestUhid,
            NextHopUhid     = "relay-uhid",
            HopCount        = 2,
            ExpiresAt       = DateTime.UtcNow.AddHours(1),
            QualityScore    = 0.9,
        };
        _store.SaveAsync(_entry).GetAwaiter().GetResult();

        // BLE metrics — cold (first sample will bootstrap).
        _bleMetrics  = new PerTransportMetrics();

        // Warm metrics — pre-seeded with 50 observations to represent
        // a transport in steady-state (most realistic bench scenario).
        _warmMetrics = new PerTransportMetrics();
        for (var i = 0; i < 50; i++)
            _warmMetrics.RecordSample(rttMs: 80 + (i % 40), success: i % 10 != 0, bytesTransferred: 512);
    }

    // ── Route store: Get (cache hit) ────────────────────────────────────

    /// <summary>
    /// ConcurrentDictionary.TryGetValue — the hot path for every packet
    /// that already has a resolved route. Should be sub-µs.
    /// </summary>
    [Benchmark]
    public async Task<RouteEntry?> RouteStore_Get_Hit()
        => await _store.GetAsync(DestUhid).ConfigureAwait(false);

    // ── Route store: Save (RREP install) ────────────────────────────────

    /// <summary>
    /// ConcurrentDictionary indexer-set — fires once per successful RREP
    /// on every routing hop.
    /// </summary>
    [Benchmark]
    public async Task RouteStore_Save()
    {
        var entry = new RouteEntry
        {
            DestinationUhid = DestUhid,
            NextHopUhid     = "relay-uhid",
            HopCount        = 2,
            ExpiresAt       = DateTime.UtcNow.AddHours(1),
            QualityScore    = 0.9,
        };
        await _store.SaveAsync(entry).ConfigureAwait(false);
    }

    // ── EWMA: RecordSample — cold (BLE bootstrap) ────────────────────────

    /// <summary>
    /// First observation on a new transport link — bootstraps EWMA from zero.
    /// Allocates a fresh <see cref="PerTransportMetrics"/> each iteration to
    /// keep the cold-start path isolated.
    /// </summary>
    [Benchmark]
    public void PerTransportMetrics_RecordSample_Cold()
    {
        var m = new PerTransportMetrics();
        m.RecordSample(rttMs: 85, success: true, bytesTransferred: 512);
    }

    // ── EWMA: RecordSample — warm (steady-state BLE observation) ────────

    /// <summary>
    /// Steady-state EWMA update — what fires on every BLE packet in flight.
    /// The metrics instance is pre-warmed so the lock-guarded branches run
    /// the full EWMA arithmetic, not the simpler bootstrap path.
    /// </summary>
    [Benchmark]
    public void PerTransportMetrics_RecordSample_Warm()
        => _warmMetrics.RecordSample(rttMs: 85, success: true, bytesTransferred: 512);

    // ── EWMA: RecordSample — failure observation ─────────────────────────

    /// <summary>
    /// Loss observation — <c>success = false</c>. Only the loss-rate EWMA
    /// is updated (RTT/throughput branches are skipped). Exercises the
    /// distinct code path the selector uses when a BLE packet is lost.
    /// </summary>
    [Benchmark]
    public void PerTransportMetrics_RecordSample_Failure()
        => _warmMetrics.RecordSample(rttMs: 0, success: false, bytesTransferred: 0);
}
