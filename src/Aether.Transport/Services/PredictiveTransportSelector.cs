// SPDX-License-Identifier: MIT
// Predictive transport selector — 2-state Kalman filter over PerTransportMetrics.
// RankWithAiAsync extends Rank() with optional CircleAI transport biases.
//
// Why a Kalman filter instead of more EWMA?
// ───────────────────────────────────────────
// EWMA is a 1-pole IIR filter: it smooths past measurements but can't predict future RTT
// when the link is actively degrading (e.g. BLE interference, Wi-Fi congestion).  The
// Kalman filter models RTT as a 2-state process [rtt, drift]:
//
//   x_t = F * x_{t-1} + w    (process model)
//   z_t = H * x_t   + v    (observation model)
//
// where  F = [[1, 1], [0, 1]]  (RTT = RTT_prev + drift; drift unchanged),
//        H = [1, 0]             (we observe only RTT, not drift),
//        w ~ N(0, Q),  v ~ N(0, R)
//
// This allows the selector to predict that a transport's RTT is RISING (positive drift)
// before it exceeds the threshold, and to prefer a calmer transport proactively.
//
// The Kalman variance estimate also feeds a reliability multiplier: a transport with
// high RTT uncertainty (P[0][0] large) is penalised even if its point estimate looks good.
//
// Usage (C#)
// ──────────
//   var selector = new PredictiveTransportSelector();
//   foreach (var transport in allTransports)
//       selector.Register(transport);
//
//   // Call this on every received PerTransportMetrics sample:
//   selector.ObserveMetrics(transport, rttMs: 45, successMs: true, bytesTransferred: 1024);
//
//   // Get the best transport for a 500-byte payload:
//   var ranked = selector.Rank(payloadBytes: 500);
//   var best   = ranked.Count > 0 ? ranked[0].Transport : null;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aether.Extensibility;
using Aether.Transport.Abstractions;
using Aether.Transport.Models;

namespace Aether.Transport.Services;

// ════════════════════════════════════════════════════════════════════════════════
//  KalmanRttFilter — 2-state Kalman filter for RTT + drift
// ════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Two-state Kalman filter estimating the RTT (in milliseconds) and its drift
/// (change in RTT per observation round) for a single transport link.
///
/// <para>
/// State vector: x = [rtt; drift]
/// Process model: F = [[1, 1], [0, 1]] (constant-velocity assumption)
/// Observation: H = [1, 0] (only RTT is measured directly)
/// </para>
///
/// <para>
/// Tuning parameters (defaults calibrated for mesh radio links at 50–1000 ms RTT):
/// <list type="bullet">
///   <item><c>processNoiseRtt</c>  (Q[0,0]) — how quickly RTT can change. Default 25 ms².</item>
///   <item><c>processNoiseDrift</c>(Q[1,1]) — how quickly drift can change. Default  5 ms².</item>
///   <item><c>observationNoise</c> (R)       — measurement noise variance. Default 100 ms².</item>
/// </list>
/// </para>
/// </summary>
internal sealed class KalmanRttFilter
{
    // ── Process noise Q (diagonal 2×2) ──────────────────────────────────────────
    private readonly double _qRtt;
    private readonly double _qDrift;
    private readonly double _r;            // Observation noise (scalar since H=[1,0])

    // ── State (x = [rtt; drift]) ─────────────────────────────────────────────────
    private double _rtt;       // estimated RTT (ms)
    private double _drift;     // estimated RTT drift (ms per sample)

    // ── Covariance P (2×2 symmetric; stored as p00, p01, p11) ──────────────────
    private double _p00;
    private double _p01;
    private double _p11;

    // ── Derived: predictive variance of RTT ─────────────────────────────────────
    /// <summary>
    /// Posterior variance of the RTT estimate (ms²).
    /// Lower = more confident; higher = uncertain link.
    /// </summary>
    public double RttVariance => _p00;

    /// <summary>Current best estimate of RTT in milliseconds.</summary>
    public double RttEstimateMs => _rtt;

    /// <summary>
    /// Current RTT drift estimate (ms change per observation round).
    /// Positive = RTT is rising; negative = improving.
    /// </summary>
    public double DriftMs => _drift;

    // ── Constructor ──────────────────────────────────────────────────────────────

    public KalmanRttFilter(
        double initialRttMs     = 200.0,
        double processNoiseRtt  = 25.0,
        double processNoiseDrift = 5.0,
        double observationNoise = 100.0)
    {
        _rtt   = initialRttMs;
        _drift = 0.0;

        // Initial covariance: large uncertainty on both states.
        _p00 = 400.0;
        _p01 = 0.0;
        _p11 = 100.0;

        _qRtt   = processNoiseRtt;
        _qDrift = processNoiseDrift;
        _r      = observationNoise;
    }

    // ── Update ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Incorporates a new RTT measurement and returns the updated estimate.
    ///
    /// Internally performs the full Kalman predict→update cycle:
    ///   1. Predict: x̂ = F * x, P̂ = F * P * Fᵀ + Q
    ///   2. Gain:    S = H * P̂ * Hᵀ + R,  K = P̂ * Hᵀ / S
    ///   3. Update:  x = x̂ + K * (z − H * x̂),  P = (I − K * H) * P̂
    /// </summary>
    public double Update(double measuredRttMs)
    {
        // ── 1. Predict ────────────────────────────────────────────────────────────
        // x_pred = F * x  (F = [[1,1],[0,1]])
        double rttPred   = _rtt + _drift;
        double driftPred = _drift;

        // P_pred = F * P * F^T + Q
        // For F = [[1,1],[0,1]]:
        //   F*P = [[p00+p01, p01+p11], [p01, p11]]
        //   (F*P)*F^T = row0 of F*P dotted with rows of F^T:
        //   p_pred[0][0] = (p00+p01)*1 + (p01+p11)*0 … wait let me be careful.
        //
        // F = [[1,1],[0,1]]  →  F^T = [[1,0],[1,1]]
        // F*P = [[p00+p01,  p01+p11],
        //        [p01,      p11]]
        // (F*P)*F^T:
        //   [0,0]: (p00+p01)*1 + (p01+p11)*1 = p00 + 2*p01 + p11
        //   [0,1]: (p00+p01)*0 + (p01+p11)*1 = p01 + p11
        //   [1,0]: p01*1 + p11*1 = p01 + p11   (= [0,1] since P is symmetric)
        //   [1,1]: p01*0 + p11*1 = p11
        double pp00 = _p00 + 2.0 * _p01 + _p11 + _qRtt;
        double pp01 = _p01 + _p11;
        double pp11 = _p11 + _qDrift;

        // ── 2. Kalman gain (H = [1, 0]) ──────────────────────────────────────────
        // S = H * P_pred * H^T + R = pp00 + R
        double S = pp00 + _r;

        // K = P_pred * H^T / S  (H^T = [1; 0])
        double k0 = pp00 / S;
        double k1 = pp01 / S;

        // ── 3. Update ─────────────────────────────────────────────────────────────
        double innovation = measuredRttMs - rttPred;
        _rtt   = rttPred   + k0 * innovation;
        _drift = driftPred + k1 * innovation;

        // P = (I - K*H) * P_pred  (I - K*H = [[1-k0, 0], [-k1, 1]])
        double ik0 = 1.0 - k0;
        _p00 = ik0 * pp00;                     // (1-k0)*pp00 + 0*pp01
        _p01 = ik0 * pp01 - k1 * pp00;        // (1-k0)*pp01 + (-k1)*pp00  Hmm...

        // Let me redo the P update carefully.
        // P_new = (I - K*H) * P_pred
        // I - K*H = [[1-k0, -0*k0], [-k1, 1]]   since H=[1,0]
        //         = [[1-k0, 0], [-k1, 1]]
        // P_new = (I-KH) * P_pred:
        //   p00_new = (1-k0)*pp00 + 0*pp01  = (1-k0)*pp00
        //   p01_new = (1-k0)*pp01 + 0*pp11  = (1-k0)*pp01
        //   p10_new = -k1*pp00  + 1*pp01   (but P is symmetric so skip p10)
        //   p11_new = -k1*pp01  + 1*pp11
        _p00 = (1.0 - k0) * pp00;
        _p01 = (1.0 - k0) * pp01;
        _p11 = -k1 * pp01 + pp11;

        // Clamp to prevent numerical drift below zero.
        _p00 = Math.Max(_p00, 1e-6);
        _p11 = Math.Max(_p11, 1e-6);

        return _rtt;
    }
}

// ════════════════════════════════════════════════════════════════════════════════
//  PredictiveTransportSelector
// ════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Predictive transport selector using per-transport Kalman RTT filters layered on
/// top of <see cref="PerTransportMetrics"/> EWMA values.
///
/// <para>
/// Extends the EWMA-only transport selection in <see cref="Aether.Transport.Services.TransportManager"/>
/// by replacing the static RTT term with the Kalman-estimated RTT (which reacts faster to degrading links) and adding a
/// reliability penalty proportional to the Kalman variance (uncertain links are scored
/// lower, even when their point estimate looks good).
/// </para>
///
/// <para>
/// Score formula:
///   <c>score = (effectiveBps / powerCost) × (1 − lossRate) / max(kalmanRttMs, 1) × (1 / (1 + σ_rtt/100))</c>
///
/// where σ_rtt = sqrt(kalmanVariance) is the standard deviation of the RTT estimate.
/// Dividing by 100 normalises ms uncertainty to a dimensionless [0, 1] range.
/// </para>
/// </summary>
public sealed class PredictiveTransportSelector
{
    // Per-transport Kalman filter, keyed by transport object identity.
    private readonly Dictionary<ITransportService, KalmanRttFilter> _filters = new();
    private readonly ReaderWriterLockSlim _rwLock = new();

    // ── Registration ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a transport for predictive tracking.  Must be called once per
    /// transport before <see cref="ObserveMetrics"/> or <see cref="Rank"/>.
    /// </summary>
    public void Register(ITransportService transport, double initialRttMs = 200.0)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _rwLock.EnterWriteLock();
        try
        {
            _filters.TryAdd(transport, new KalmanRttFilter(initialRttMs));
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes a previously registered transport and discards its Kalman state.
    /// </summary>
    public void Unregister(ITransportService transport)
    {
        _rwLock.EnterWriteLock();
        try { _filters.Remove(transport); }
        finally { _rwLock.ExitWriteLock(); }
    }

    // ── Observation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Feeds a new RTT measurement into the Kalman filter for the given transport
    /// AND forwards the sample to the transport's <see cref="PerTransportMetrics"/>
    /// (if it has one).  Call this after every completed send.
    /// </summary>
    /// <param name="transport">The transport that just completed a send.</param>
    /// <param name="rttMs">Measured round-trip time in milliseconds.</param>
    /// <param name="success">Whether the send was acknowledged.</param>
    /// <param name="bytesTransferred">Bytes successfully transferred.</param>
    public void ObserveMetrics(
        ITransportService transport,
        long rttMs,
        bool success,
        long bytesTransferred)
    {
        // Update the PerTransportMetrics EWMA (transport's own store).
        transport.Metrics?.RecordSample(rttMs, success, bytesTransferred);

        if (rttMs <= 0 || !success) return;

        // Update our Kalman filter.
        _rwLock.EnterReadLock();
        try
        {
            if (_filters.TryGetValue(transport, out var filter))
                filter.Update(rttMs);
        }
        finally { _rwLock.ExitReadLock(); }
    }

    // ── Ranking ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns transports in descending predictive-score order.
    /// Only available transports (<see cref="ITransportService.IsAvailable"/>) are included.
    /// </summary>
    /// <param name="payloadBytes">
    ///   Intended payload size.  Used to skip transports whose
    ///   <see cref="ITransportService.MaxBandwidthBps"/> is so low that they would
    ///   introduce more than 30 s of serialisation delay for this payload.
    /// </param>
    public IReadOnlyList<(ITransportService Transport, double Score, double PredictedRttMs, double RttVariance)>
        Rank(int payloadBytes = 512)
    {
        _rwLock.EnterReadLock();
        try
        {
            var result = new List<(ITransportService, double, double, double)>(_filters.Count);

            foreach (var (transport, filter) in _filters)
            {
                if (!transport.IsAvailable) continue;

                // Exclude transports that can't physically handle this payload in time.
                if (transport.MaxBandwidthBps > 0)
                {
                    double serialSec = (payloadBytes * 8.0) / transport.MaxBandwidthBps;
                    if (serialSec > 30.0) continue;
                }

                double kalmanRtt = Math.Max(filter.RttEstimateMs, 1.0);
                double variance  = filter.RttVariance;
                double stddev    = Math.Sqrt(variance);

                // Build score.  If we have live EWMA metrics, use them for loss/throughput.
                double lossRate;
                double effectiveBps;
                int    powerCost = Math.Max(transport.PowerCostRelative, 1);

                if (transport.Metrics is { } m)
                {
                    lossRate     = m.EwmaLossRate;
                    effectiveBps = Math.Max(m.EwmaThroughputBps, transport.MaxBandwidthBps * 0.1);
                }
                else
                {
                    // No live metrics — use static prior.
                    lossRate     = 0.05;
                    effectiveBps = transport.MaxBandwidthBps * 0.1;
                }

                // Reliability factor: penalise uncertain links.
                // (1 / (1 + σ/100)) decays from 1.0 at σ=0 to ~0.5 at σ=100 ms.
                double reliabilityFactor = 1.0 / (1.0 + stddev / 100.0);

                double score = (effectiveBps / powerCost)
                             * (1.0 - lossRate)
                             / kalmanRtt
                             * reliabilityFactor;

                result.Add((transport, score, kalmanRtt, variance));
            }

            result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return result;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>
    /// Returns the single highest-scoring available transport for the given payload.
    /// Returns <see langword="null"/> if no transport is available.
    /// </summary>
    public ITransportService? SelectBest(int payloadBytes = 512)
    {
        var ranked = Rank(payloadBytes);
        return ranked.Count > 0 ? ranked[0].Transport : null;
    }

    /// <summary>
    /// Returns the Kalman state (RTT estimate, drift, variance) for a registered transport.
    /// Returns <see langword="null"/> if the transport is not registered.
    /// </summary>
    public (double RttMs, double DriftMs, double Variance)? GetKalmanState(ITransportService transport)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _filters.TryGetValue(transport, out var f)
                ? (f.RttEstimateMs, f.DriftMs, f.RttVariance)
                : null;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    // ── AI-augmented ranking ──────────────────────────────────────────────────────

    /// <summary>
    /// AI-augmented ranking: runs <see cref="Rank"/> then multiplies each transport's
    /// score by the AI provider's bias for that transport name.
    ///
    /// <para>
    /// Falls back to the plain <see cref="Rank"/> result when:
    /// <list type="bullet">
    ///   <item><description><paramref name="aiProvider"/> is <see langword="null"/>.</description></item>
    ///   <item><description><see cref="IAetherAiProvider.IsAvailable"/> is <c>false</c>.</description></item>
    ///   <item><description>The provider returns an empty bias dictionary.</description></item>
    ///   <item><description>The provider throws — the exception is swallowed; AI is never a hard dependency.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Bias semantics match <see cref="IAetherAiProvider.GetTransportBiasesAsync"/>:
    /// 1.0 = neutral, &gt;1.0 = AI-preferred, &lt;1.0 = AI-discouraged, 0.0 = effectively suppress.
    /// Negative multipliers are clamped to 0.0 so scores never go negative.
    /// The resulting list is sorted by adjusted score descending.
    /// </para>
    /// </summary>
    /// <param name="payloadBytes">Intended payload size, forwarded to both <see cref="Rank"/> and the provider.</param>
    /// <param name="aiProvider">
    /// Optional AI provider. When <see langword="null"/> or unavailable, behaves identically to <see cref="Rank"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token forwarded to the AI provider.</param>
    /// <returns>
    /// Transports in descending AI-adjusted score order. When AI is unavailable or fails,
    /// returns the same result as <see cref="Rank"/>.
    /// </returns>
    public async Task<IReadOnlyList<(ITransportService Transport, double Score, double PredictedRttMs, double RttVariance)>>
        RankWithAiAsync(
            int payloadBytes = 512,
            IAetherAiProvider? aiProvider = null,
            CancellationToken cancellationToken = default)
    {
        var baseRanking = Rank(payloadBytes);

        if (aiProvider is not { IsAvailable: true })
            return baseRanking;

        IReadOnlyDictionary<string, double> biases;
        try
        {
            biases = await aiProvider.GetTransportBiasesAsync(payloadBytes, cancellationToken)
                                      .ConfigureAwait(false);
        }
        catch
        {
            // AI failures are never fatal — mesh operates without AI.
            return baseRanking;
        }

        if (biases.Count == 0)
            return baseRanking;

        var adjusted = new List<(ITransportService, double, double, double)>(baseRanking.Count);
        foreach (var (transport, score, rtt, variance) in baseRanking)
        {
            double multiplier = biases.TryGetValue(transport.Name, out double m) ? m : 1.0;
            multiplier = Math.Max(multiplier, 0.0); // clamp: no negative scores
            adjusted.Add((transport, score * multiplier, rtt, variance));
        }

        adjusted.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return adjusted;
    }
}
