// SPDX-License-Identifier: MIT

namespace Aether.Extensibility.Events;

/// <summary>Mesh-wide topology and congestion observations.</summary>
public enum AetherNetworkEventKind : byte
{
    /// <summary>The set of reachable nodes or routes has changed materially.</summary>
    TopologyChanged,

    /// <summary>Network congestion has crossed a notable threshold.</summary>
    CongestionDetected,

    /// <summary>The mesh has split into two or more isolated partitions.</summary>
    PartitionDetected,
}

/// <summary>
/// Emitted when the mesh topology or overall network health changes.
/// Provides aggregate context that the AI layer uses alongside individual
/// <see cref="AetherNodeEvent"/> and <see cref="AetherRouteEvent"/> feeds
/// to produce <see cref="AiNetworkHealthReport"/> snapshots.
/// </summary>
/// <param name="Kind">The category of network-level change.</param>
/// <param name="NodeCount">Total number of peers currently reachable.</param>
/// <param name="ActiveRouteCount">Number of active non-expired routes in the route table.</param>
/// <param name="CongestionLevel">Estimated congestion from 0.0 (idle) to 1.0 (saturated).</param>
/// <param name="OccurredAt">UTC timestamp of the event.</param>
public sealed record AetherNetworkEvent(
    AetherNetworkEventKind Kind,
    int                    NodeCount,
    int                    ActiveRouteCount,
    double                 CongestionLevel,
    DateTimeOffset         OccurredAt)
{
    /// <summary>
    /// <c>true</c> when <see cref="CongestionLevel"/> exceeds 0.75 — a useful default alert
    /// threshold. Callers may apply their own thresholds.
    /// </summary>
    public bool IsHighCongestion => CongestionLevel > 0.75;
}
