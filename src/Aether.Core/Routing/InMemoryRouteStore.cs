// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Aether.Models;

namespace Aether.Routing;

/// <summary>
/// Thread-safe, process-local route store. Sufficient for tests, demos, and any
/// host that does not need routes to survive a restart.
/// </summary>
public sealed class InMemoryRouteStore : IRouteStore
{
    private readonly ConcurrentDictionary<string, RouteEntry> _routes = new(StringComparer.Ordinal);

    public Task<RouteEntry?> GetAsync(string destinationUhid, CancellationToken cancellationToken = default)
    {
        _routes.TryGetValue(destinationUhid, out var route);
        return Task.FromResult(route);
    }

    public Task<IReadOnlyList<RouteEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RouteEntry> snapshot = _routes.Values.ToArray();
        return Task.FromResult(snapshot);
    }

    public Task SaveAsync(RouteEntry route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        _routes[route.DestinationUhid] = route;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string destinationUhid, CancellationToken cancellationToken = default)
    {
        _routes.TryRemove(destinationUhid, out _);
        return Task.CompletedTask;
    }

    public Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var kvp in _routes)
        {
            if (kvp.Value.IsExpired && _routes.TryRemove(kvp.Key, out _))
                removed++;
        }
        return Task.FromResult(removed);
    }
}
