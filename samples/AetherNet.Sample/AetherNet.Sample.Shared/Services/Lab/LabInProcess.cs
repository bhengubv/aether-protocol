// SPDX-License-Identifier: MIT

using AetherNet.Extensibility;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// The two tiny seams the content/forge services expect but a single-page in-process demo has no
/// real need for. Both are honest no-ops, not stubs of the protocol: the content service is happy to
/// broadcast when a route lookup returns nothing, and the incentive ledger simply isn't kept.
///
/// <para>They live once, here, because <c>/lab/files</c> and <c>/lab/forge</c> both stand up a real
/// <see cref="AetherNet.Content.ContentService"/> over the in-process transport, and that constructor
/// wants an <see cref="IRoutingService"/> it will never get to use — every send in these demos is a
/// flood to directly-reachable peers, which is exactly what a null route falls back to.</para>
/// </summary>
internal sealed class NullRoutingService : IRoutingService
{
    /// <summary>No cached routes exist, so discovery "fails" and the caller broadcasts — the demo's intent.</summary>
    public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default)
        => Task.FromResult<RouteEntry?>(null);

    public RouteEntry? GetCachedRoute(string destinationUhid) => null;

    public IReadOnlyList<RouteEntry> GetAllRoutes() => Array.Empty<RouteEntry>();

    public Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The relay-reward ledger, not kept. <see cref="IAetherNetIncentiveProvider"/> ships a default
/// implementation for every member, so an empty derivation is a complete, correct "accounting off"
/// — which is what a demo that isn't modelling the economy wants.
/// </summary>
internal sealed class NoOpIncentives : IAetherNetIncentiveProvider
{
}
