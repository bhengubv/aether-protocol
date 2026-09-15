// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The routing <see cref="MessagingService"/> needs, on a phone-to-phone radio that has exactly one link.
///
/// <para>
/// AODV-style route discovery (<see cref="PacketType.RouteRequest"/>/<see cref="PacketType.RouteReply"/>
/// flooding) buys nothing here: <see cref="RadioMeshSender"/> pushes every packet to the one peer on the
/// link regardless of the next hop, and reaching a phone that is not that peer is the relay's job — a
/// third node carries the packet one hop further (<c>AndroidRadioMesh.Carry</c> / the library
/// <see cref="MeshRelay"/>), it is not something this node route-discovers. So the "route" to anyone is
/// always the same: hand it to the link and let the mesh carry it. This shim answers exactly that, which
/// is what lets the reliable messaging core run over the app's radios without a routing table it would
/// never populate.
/// </para>
/// </summary>
public sealed class OneHopRoutingService : IRoutingService
{
    private static RouteEntry? RouteTo(string destinationUhid) =>
        string.IsNullOrEmpty(destinationUhid)
            ? null
            : new RouteEntry { DestinationUhid = destinationUhid, NextHopUhid = destinationUhid, HopCount = 1 };

    public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default) =>
        Task.FromResult(RouteTo(destinationUhid));

    public RouteEntry? GetCachedRoute(string destinationUhid) => RouteTo(destinationUhid);

    public IReadOnlyList<RouteEntry> GetAllRoutes() => [];

    // Nothing floods route control here — there is no table to build and no multi-hop discovery to run.
    public Task HandleRouteRequestAsync(MeshPacket packet, string? linkSenderUhid = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HandleRouteReplyAsync(MeshPacket packet, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
