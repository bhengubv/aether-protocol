// SPDX-License-Identifier: MIT

namespace AetherNet.Models;

/// <summary>
/// A single entry in the mesh routing table. Routes are discovered via
/// AODV-inspired route request/reply exchanges and expire after a configurable timeout.
/// </summary>
public sealed class RouteEntry
{
    /// <summary>The UHID of the final destination this route reaches.</summary>
    public string DestinationUhid { get; set; } = string.Empty;

    /// <summary>The UHID of the immediate next-hop peer to forward packets toward the destination.</summary>
    public string NextHopUhid { get; set; } = string.Empty;

    /// <summary>Number of hops from here to the destination along this route.</summary>
    public int HopCount { get; set; }

    /// <summary>Estimated round-trip latency to the destination in milliseconds.</summary>
    public double LatencyMs { get; set; }

    /// <summary>
    /// Composite quality score from 0.0 (worst) to 1.0 (best).
    /// Factors in hop count, latency, and next-hop reliability.
    /// </summary>
    public double QualityScore { get; set; } = 1.0;

    /// <summary>UTC time after which this route is considered stale and should be re-discovered.</summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddSeconds(300);

    /// <summary>Returns true if this route has expired.</summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Refreshes the expiry time by the given number of seconds from now.
    /// </summary>
    public void Refresh(int expirySeconds = 300)
    {
        ExpiresAt = DateTime.UtcNow.AddSeconds(expirySeconds);
    }

    /// <summary>
    /// Computes quality score based on hop count, latency, and peer reliability.
    /// </summary>
    public static double ComputeQuality(int hopCount, double latencyMs, double peerReliability)
    {
        // Hop penalty: each hop beyond 1 reduces quality by 10%
        var hopFactor = Math.Max(0.0, 1.0 - (hopCount - 1) * 0.1);

        // Latency penalty: quality degrades as latency increases beyond 100ms
        var latencyFactor = latencyMs <= 100 ? 1.0 : Math.Max(0.0, 1.0 - (latencyMs - 100) / 2000.0);

        return Math.Clamp(hopFactor * 0.3 + latencyFactor * 0.3 + peerReliability * 0.4, 0.0, 1.0);
    }

    public override string ToString() =>
        $"Route to {DestinationUhid} via {NextHopUhid} hops={HopCount} latency={LatencyMs:F1}ms quality={QualityScore:F2}";
}
