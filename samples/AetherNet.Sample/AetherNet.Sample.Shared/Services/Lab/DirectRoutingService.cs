// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// A one-hop routing table for the in-process Lab meshes. Every node in these demos is a
/// direct neighbour of every other, so the next hop toward any destination simply <i>is</i>
/// that destination — there is no multi-hop path to discover, and no RREQ/RREP round-trip to
/// simulate. The streaming services (<c>StreamingService</c>, <c>WatchTogetherService</c>,
/// <c>GroupVideoService</c>, <c>VideoCallService</c>) all ask this for a route and, finding one,
/// <b>unicast</b> to a single subscriber / participant instead of falling back to a flood —
/// which is what lets the demos show precise, addressed delivery (a viewer who never subscribed
/// gets nothing) rather than everyone hearing everything.
/// </summary>
internal sealed class DirectRoutingService : IRoutingService
{
    public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default)
        => Task.FromResult(Direct(destinationUhid));

    public RouteEntry? GetCachedRoute(string destinationUhid) => Direct(destinationUhid);

    public IReadOnlyList<RouteEntry> GetAllRoutes() => Array.Empty<RouteEntry>();

    // The mesh here is complete and static, so there is nothing to learn from an RREQ/RREP and
    // nothing to expire. These exist only to satisfy the interface the services depend on.
    public Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static RouteEntry? Direct(string destinationUhid)
        => string.IsNullOrEmpty(destinationUhid)
            ? null
            : new RouteEntry
            {
                DestinationUhid = destinationUhid,
                NextHopUhid = destinationUhid,
                HopCount = 1,
                QualityScore = 1.0,
            };
}
