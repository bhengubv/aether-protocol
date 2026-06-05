// SPDX-License-Identifier: MIT

namespace AetherMesh.Extensibility.Events;

/// <summary>Kinds of node lifecycle transitions Aether can emit.</summary>
public enum AetherNodeEventKind : byte
{
    /// <summary>A new peer has joined and completed handshake.</summary>
    Joined,

    /// <summary>A peer has disconnected or timed out.</summary>
    Left,

    /// <summary>An existing peer's health metrics have changed.</summary>
    HealthChanged,
}

/// <summary>
/// Point-in-time health snapshot for a single mesh node.
/// </summary>
/// <param name="TrustScore">
///   0.0 (untrusted) to 1.0 (fully trusted). Maintained by the AI Security
///   Layer when active; defaults to 1.0 for all nodes when the security layer
///   is not registered.
/// </param>
/// <param name="IsReachable">Whether the node is currently reachable.</param>
/// <param name="Latency">Last measured round-trip latency to the node.</param>
/// <param name="HopCount">Number of hops to reach this node from the local node.</param>
public sealed record AetherNodeHealth(
    double    TrustScore,
    bool      IsReachable,
    TimeSpan  Latency,
    int       HopCount)
{
    /// <summary>Returns <c>true</c> when <see cref="TrustScore"/> is within the valid 0–1 range.</summary>
    public bool IsValid => TrustScore is >= 0.0 and <= 1.0;
}

/// <summary>
/// Emitted by Aether whenever a node joins, leaves, or changes health.
/// Consumed by <see cref="IAetherTelemetry"/> subscribers — the AI layer
/// never writes back into Aether directly.
/// </summary>
/// <param name="NodeId">UHID of the node this event describes.</param>
/// <param name="Kind">The lifecycle change that triggered this event.</param>
/// <param name="Health">Current health snapshot at the time of the event.</param>
/// <param name="OccurredAt">UTC timestamp of the event.</param>
public sealed record AetherNodeEvent(
    string            NodeId,
    AetherNodeEventKind Kind,
    AetherNodeHealth  Health,
    DateTimeOffset    OccurredAt)
{
    /// <summary>Convenience: <c>true</c> when this is a departure event.</summary>
    public bool IsExit => Kind is AetherNodeEventKind.Left;
}
