// SPDX-License-Identifier: MIT

namespace AetherMesh.Extensibility.Events;

/// <summary>Kinds of routing changes Aether can emit.</summary>
public enum AetherRouteEventKind : byte
{
    /// <summary>A new route to a destination was discovered via AODV.</summary>
    Discovered,

    /// <summary>An existing route was updated (better path, lower hop count, etc.).</summary>
    Changed,

    /// <summary>A route expired or became unreachable.</summary>
    Failed,
}

/// <summary>
/// Emitted when Aether discovers, updates, or loses a route between two nodes.
/// <see cref="Path"/> describes the full sequence of UHID hops traversed.
///
/// <para>
/// The AI layer uses route events to build a mesh topology model that informs
/// <see cref="IAetherAiProvider.SuggestRoutesAsync"/> recommendations.
/// </para>
/// </summary>
/// <param name="SourceNodeId">UHID of the originating node.</param>
/// <param name="DestinationNodeId">UHID of the destination node.</param>
/// <param name="Path">Ordered list of UHID hops (source-inclusive, destination-inclusive).</param>
/// <param name="Kind">The nature of the routing change.</param>
/// <param name="FailureReason">Human-readable reason for a <see cref="AetherRouteEventKind.Failed"/> event; <c>null</c> otherwise.</param>
/// <param name="OccurredAt">UTC timestamp of the event.</param>
public sealed record AetherRouteEvent(
    string                 SourceNodeId,
    string                 DestinationNodeId,
    IReadOnlyList<string>  Path,
    AetherRouteEventKind   Kind,
    string?                FailureReason,
    DateTimeOffset         OccurredAt)
{
    /// <summary>Number of hops in this route, including source and destination.</summary>
    public int HopCount => Path.Count;

    /// <summary><c>true</c> when this event represents a routing failure.</summary>
    public bool IsFailed => Kind is AetherRouteEventKind.Failed;
}
