// SPDX-License-Identifier: MIT
using AetherMesh.Space.Models;

namespace AetherMesh.Space;

/// <summary>
/// Geo-pinned community noticeboards (aether-space Phase-2 extension).
///
/// Nodes drop breadcrumbs at geohash coordinates. Passing devices auto-pull
/// and re-host them for other passersby. All propagation is fully offline —
/// no internet required.
///
/// Propagation rules (enforced by implementations):
/// <list type="bullet">
///   <item>Forward only if the peer's last-known geohash is within 3 cells
///     of the breadcrumb's geohash (prevents global flooding).</item>
///   <item>TTL is enforced; expired breadcrumbs are pruned on startup and
///     every 6 hours.</item>
///   <item><see cref="BreadcrumbType.Emergency"/> bypasses the 3-cell radius
///     limit — propagates to all reachable peers.</item>
/// </list>
/// </summary>
public interface ISpaceService
{
    /// <summary>
    /// Drop a new breadcrumb at the given geohash location.
    /// </summary>
    /// <param name="geoHash">6-character geohash (~1.2 km² cell).</param>
    /// <param name="contentHash">IContentService hash of the payload.</param>
    /// <param name="anchorUhid">UHID of the creator node.</param>
    /// <param name="type">Category of the breadcrumb.</param>
    /// <param name="ttlHours">
    /// Time-to-live in hours (1–168; ignored for Emergency, which uses 720 h).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<SpaceBreadcrumb> DropAsync(
        string geoHash,
        string contentHash,
        string anchorUhid,
        BreadcrumbType type = BreadcrumbType.Notice,
        int ttlHours = 72,
        CancellationToken ct = default);

    /// <summary>
    /// Scan for active (non-expired) breadcrumbs centred on <paramref name="centerGeoHash"/>.
    /// </summary>
    /// <param name="centerGeoHash">6-character geohash of the scan origin.</param>
    /// <param name="radiusCells">
    /// Number of adjacent cells to include in the scan (default 1 = centre + 8 neighbours).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SpaceBreadcrumb>> ScanAsync(
        string centerGeoHash,
        int radiusCells = 1,
        CancellationToken ct = default);

    /// <summary>
    /// Manually cache and re-host a breadcrumb received from a peer.
    /// </summary>
    Task PinAsync(SpaceBreadcrumb breadcrumb, CancellationToken ct = default);

    /// <summary>
    /// Delete a breadcrumb. Only succeeds if <paramref name="requestorUhid"/>
    /// matches the breadcrumb's <see cref="SpaceBreadcrumb.AnchorUhid"/> (creator-only delete).
    /// Returns <c>true</c> if deleted, <c>false</c> if not found or not permitted.
    /// </summary>
    Task<bool> DeleteAsync(SpaceBreadcrumb breadcrumb, string requestorUhid, CancellationToken ct = default);

    /// <summary>Prune all expired breadcrumbs from the local store. Returns the count removed.</summary>
    int PruneExpired();

    /// <summary>Fired when a new breadcrumb is received from the mesh or dropped locally.</summary>
    event EventHandler<SpaceBreadcrumb> BreadcrumbReceived;

    /// <summary>Fired when a locally cached breadcrumb passes its TTL.</summary>
    event EventHandler<SpaceBreadcrumb> BreadcrumbExpired;
}
