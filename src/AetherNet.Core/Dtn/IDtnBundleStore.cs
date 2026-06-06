// SPDX-License-Identifier: MIT

using AetherNet.Models;

namespace AetherNet.Dtn;

/// <summary>
/// Persistent backing store for DTN bundles and their custody records.
/// Production hosts substitute SQLite- or file-backed implementations so bundles
/// survive restarts; the default <see cref="InMemoryDtnBundleStore"/> is process-local.
/// </summary>
public interface IDtnBundleStore
{
    /// <summary>Returns the bundle with the given id, or null if absent.</summary>
    Task<DtnBundle?> GetAsync(Guid bundleId, CancellationToken cancellationToken = default);

    /// <summary>Returns every bundle whose <see cref="DtnBundle.Status"/> is <see cref="BundleStatus.Pending"/> or <see cref="BundleStatus.InCustody"/>.</summary>
    Task<IReadOnlyList<DtnBundle>> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a bundle by id.</summary>
    Task SaveAsync(DtnBundle bundle, CancellationToken cancellationToken = default);

    /// <summary>Removes the bundle with the given id, if present.</summary>
    Task RemoveAsync(Guid bundleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the count of active (non-delivered, non-expired) bundles currently held.</summary>
    Task<int> GetActiveCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a custody transfer.</summary>
    Task SaveCustodyAsync(CustodyRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns every custody record associated with the given bundle.</summary>
    Task<IReadOnlyList<CustodyRecord>> GetCustodyRecordsAsync(Guid bundleId, CancellationToken cancellationToken = default);

    /// <summary>Marks every expired bundle as <see cref="BundleStatus.Expired"/> and returns the count affected.</summary>
    Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default);
}
