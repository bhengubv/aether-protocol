// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;

namespace AetherNet.Core.Tests.Fakes;

/// <summary>
/// Minimal in-memory <see cref="IRoutingService"/> for streaming/voice/video tests.
/// Returns whatever route the test pre-installs, otherwise null (forcing the
/// caller to fall back to broadcast).
/// </summary>
public sealed class FakeRoutingService : IRoutingService
{
    private readonly ConcurrentDictionary<string, RouteEntry> _routes = new(StringComparer.Ordinal);

    /// <summary>
    /// Pre-populate a direct route to <paramref name="destination"/> so that
    /// FindRouteAsync returns a non-null entry pointing at <paramref name="nextHop"/>.
    /// </summary>
    public void SetRoute(string destination, string nextHop, int hopCount = 1)
    {
        _routes[destination] = new RouteEntry
        {
            DestinationUhid = destination,
            NextHopUhid = nextHop,
            HopCount = hopCount,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };
    }

    public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default)
    {
        _routes.TryGetValue(destinationUhid, out var entry);
        return Task.FromResult<RouteEntry?>(entry);
    }

    public RouteEntry? GetCachedRoute(string destinationUhid)
    {
        _routes.TryGetValue(destinationUhid, out var entry);
        return entry;
    }

    public IReadOnlyList<RouteEntry> GetAllRoutes() => _routes.Values.ToArray();

    public Task HandleRouteRequestAsync(MeshPacket routeRequest, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PruneAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
