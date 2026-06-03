// SPDX-License-Identifier: MIT
using Aether.Forge.Models;

namespace Aether.Forge;

/// <summary>
/// Mesh-native package cache proxy (aether-forge Phase-2 extension).
///
/// First internet pull is cached as Aether content chunks; subsequent pulls
/// by anyone in the mesh are served locally at mesh speeds, consuming zero
/// mobile data.
///
/// Supported ecosystems: npm, pip, cargo, go, nuget, git.
/// </summary>
public interface IForgeService
{
    /// <summary>
    /// Look up a cached entry by package ID.
    /// Returns <c>null</c> if the package is not cached locally.
    /// </summary>
    Task<ForgeEntry?> QueryAsync(string packageId, CancellationToken ct = default);

    /// <summary>
    /// Store a new artifact in the mesh cache. If an entry with the same
    /// <paramref name="packageId"/> already exists, the existing entry is
    /// returned unchanged (idempotent — first write wins).
    /// </summary>
    /// <param name="packageId">Namespaced package ID (<c>npm:react@18.2.0</c> etc).</param>
    /// <param name="contentHash">Aether content hash of the cached bytes.</param>
    /// <param name="sizeBytes">Size of the artifact in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ForgeEntry> CacheAsync(string packageId, string contentHash, long sizeBytes, CancellationToken ct = default);

    /// <summary>
    /// Increment the download counter for <paramref name="packageId"/> and return the entry.
    /// Returns <c>null</c> if the package is not cached.
    /// </summary>
    Task<ForgeEntry?> FetchAsync(string packageId, CancellationToken ct = default);

    /// <summary>Returns current aggregate cache statistics.</summary>
    Task<ForgeStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Fired when a new artifact is added to the local cache via <see cref="CacheAsync"/>.</summary>
    event EventHandler<ForgeEntry> NewEntryAnnounced;
}
