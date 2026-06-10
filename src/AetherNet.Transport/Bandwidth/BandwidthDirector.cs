// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Bandwidth;

namespace AetherNet.Transport.Bandwidth;

/// <summary>
/// Cross-transport bandwidth synthesis and mesh gossip coordinator.
///
/// <para>
/// Maintains a matrix of (peerUhid × transportName) → <see cref="BandwidthSample"/> estimates
/// and provides transport recommendations based on payload size, BDP, and power cost.
/// </para>
///
/// <para>
/// Transport selection algorithm:
/// <list type="number">
///   <item>Score = AvailableBps / PowerCostRelative (higher is better).</item>
///   <item>If payload &gt; BDP: prefer the transport with the largest BDP (reduces round-trips).</item>
///   <item>Penalise transports with <see cref="BandwidthConfidence.None"/> by 50% (untrusted estimate).</item>
/// </list>
/// </para>
/// </summary>
public sealed class BandwidthDirector : IBandwidthDirector
{
    // (peerUhid, transportName) → latest sample
    private readonly ConcurrentDictionary<(string peer, string transport), BandwidthSample>
        _matrix = new();

    private readonly ConcurrentDictionary<string, IBandwidthEstimator>
        _estimators = new(StringComparer.OrdinalIgnoreCase);

    // Power costs per transport name (lower = preferred). Defaults mirror ITransportService conventions.
    private static readonly Dictionary<string, int> DefaultPowerCosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NearLink"]    = 1,
        ["BLE"]         = 2,
        ["Wi-Fi Direct"]= 3,
        ["CircleLink"]  = 3,
        ["QUIC Relay"]  = 10,
        ["HTTP Relay"]  = 10,
    };

    // ── IBandwidthDirector ───────────────────────────────────────────────────

    public void Register(IBandwidthEstimator estimator)
    {
        _estimators[estimator.TransportName] = estimator;
        estimator.SampleImproved += (_, sample) =>
        {
            // When any estimator fires, update every known peer's entry for this transport.
            foreach (var key in _matrix.Keys.Where(k =>
                string.Equals(k.transport, sample.TransportName, StringComparison.OrdinalIgnoreCase)))
            {
                _matrix[key] = sample;
            }
        };
    }

    public BandwidthSample? GetEstimate(string peerUhid, string transportName)
    {
        _matrix.TryGetValue((peerUhid, transportName), out var sample);
        return sample;
    }

    public IReadOnlyList<BandwidthSample> GetEstimates(string peerUhid)
    {
        return _matrix
            .Where(kv => string.Equals(kv.Key.peer, peerUhid, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .OrderByDescending(s => s.AvailableBps)
            .ToList();
    }

    public string? RecommendTransport(string peerUhid, long payloadBytes)
    {
        var candidates = GetEstimates(peerUhid);
        if (candidates.Count == 0)
        {
            // No measurement data yet — fall back to the registered transport with lowest power cost.
            return _estimators.Values
                .OrderBy(e => DefaultPowerCosts.GetValueOrDefault(e.TransportName, 5))
                .FirstOrDefault()?.TransportName;
        }

        BandwidthSample? best = null;
        var bestScore = double.MinValue;

        foreach (var s in candidates)
        {
            var powerCost = (double)DefaultPowerCosts.GetValueOrDefault(s.TransportName, 5);
            var available = (double)s.AvailableBps;

            // Prefer larger BDP for large payloads.
            var bdpBonus = payloadBytes > s.BdpBytes ? 0.0 : 1.5;

            // Penalise untrusted estimates.
            var confidenceFactor = s.Confidence == BandwidthConfidence.None ? 0.5 : 1.0;

            var score = (available / powerCost) * bdpBonus * confidenceFactor;

            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }

        return best?.TransportName;
    }

    public BandwidthGossipPayload? BuildGossipPayload(string peerUhid, string transportName)
    {
        if (!_estimators.TryGetValue(transportName, out var estimator)) return null;
        var s = estimator.CurrentSample;
        if (s.Confidence == BandwidthConfidence.None) return null;

        return new BandwidthGossipPayload(
            peerUhid,
            transportName,
            s.BtlBwBps,
            (long)s.RtProp.TotalMicroseconds,
            s.Confidence,
            s.MeasuredAt);
    }

    public void ApplyGossip(BandwidthGossipPayload payload)
    {
        if (!_estimators.TryGetValue(payload.TransportName, out var estimator)) return;
        estimator.WarmFromGossip(
            payload.BtlBwBps,
            TimeSpan.FromMicroseconds(payload.RtPropUs),
            payload.Confidence);

        // Seed the matrix so GetEstimate returns something even before we probe.
        var key = (payload.PeerUhid, payload.TransportName);
        _matrix[key] = estimator.CurrentSample;
    }
}
