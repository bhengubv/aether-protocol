// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;

namespace AetherNet.Routing;

/// <summary>
/// Discovers and maintains routes through the mesh using AODV-inspired RREQ/RREP exchanges.
///
/// Lifecycle:
///   * The host calls <see cref="FindRouteAsync"/> when it needs a route to a destination.
///     If the route is in cache it returns immediately; otherwise an RREQ is broadcast and
///     the call awaits the matching RREP (subject to <see cref="Constants.ProtocolConstants.RouteTimeoutMs"/>).
///   * The host pumps incoming RREQs and RREPs into <see cref="HandleRouteRequestAsync"/>
///     and <see cref="HandleRouteReplyAsync"/> respectively.
///   * The host periodically calls <see cref="PruneAsync"/> to clear expired routes and
///     trim the RREQ deduplication cache.
/// </summary>
public interface IRoutingService
{
    /// <summary>
    /// Returns a route to <paramref name="destinationUhid"/>, discovering one via RREQ/RREP if necessary.
    /// Returns null if no route was found within <see cref="Constants.ProtocolConstants.RouteTimeoutMs"/>.
    /// </summary>
    Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default);

    /// <summary>Synchronous, cache-only lookup. Returns null when not cached or expired.</summary>
    RouteEntry? GetCachedRoute(string destinationUhid);

    /// <summary>Snapshot of every non-expired route currently cached in memory.</summary>
    IReadOnlyList<RouteEntry> GetAllRoutes();

    /// <summary>Process an incoming RREQ. Installs a reverse route, replies if we are the destination, otherwise forwards.</summary>
    Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken cancellationToken = default);

    /// <summary>Process an incoming RREP. Installs the forward route, completes any pending FindRouteAsync, otherwise forwards.</summary>
    Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default);

    /// <summary>Remove expired routes from memory and the route store, and trim RREQ dedup state.</summary>
    Task PruneAsync(CancellationToken cancellationToken = default);
}
