// SPDX-License-Identifier: MIT

using AetherMesh.Models;

namespace AetherMesh.Routing;

/// <summary>
/// Persistent backing store for the routing table. The default implementation
/// (<see cref="InMemoryRouteStore"/>) is process-local; production hosts typically
/// substitute a SQLite- or file-backed implementation so routes survive restarts.
/// </summary>
public interface IRouteStore
{
    /// <summary>Returns the route to <paramref name="destinationUhid"/>, or null if none is stored.</summary>
    Task<RouteEntry?> GetAsync(string destinationUhid, CancellationToken cancellationToken = default);

    /// <summary>Returns every route currently stored, including expired ones (pruning is the caller's responsibility).</summary>
    Task<IReadOnlyList<RouteEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the route for <see cref="RouteEntry.DestinationUhid"/>.</summary>
    Task SaveAsync(RouteEntry route, CancellationToken cancellationToken = default);

    /// <summary>Removes the route for <paramref name="destinationUhid"/>, if present.</summary>
    Task RemoveAsync(string destinationUhid, CancellationToken cancellationToken = default);

    /// <summary>Removes every route whose <see cref="RouteEntry.ExpiresAt"/> is in the past.</summary>
    Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default);
}
