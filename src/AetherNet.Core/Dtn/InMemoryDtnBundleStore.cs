// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Models;

namespace AetherNet.Dtn;

/// <summary>
/// Thread-safe, process-local DTN bundle store. Sufficient for tests, demos,
/// and any host that does not need bundles to survive a restart.
/// </summary>
public sealed class InMemoryDtnBundleStore : IDtnBundleStore
{
    private readonly ConcurrentDictionary<Guid, DtnBundle> _bundles = new();
    private readonly ConcurrentDictionary<Guid, CustodyRecord> _custody = new();

    public Task<DtnBundle?> GetAsync(Guid bundleId, CancellationToken cancellationToken = default)
    {
        _bundles.TryGetValue(bundleId, out var bundle);
        return Task.FromResult(bundle);
    }

    public Task<IReadOnlyList<DtnBundle>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DtnBundle> active = _bundles.Values
            .Where(b => b.Status is BundleStatus.Pending or BundleStatus.InCustody && !b.IsExpired)
            .ToArray();
        return Task.FromResult(active);
    }

    public Task SaveAsync(DtnBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _bundles[bundle.Id] = bundle;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid bundleId, CancellationToken cancellationToken = default)
    {
        _bundles.TryRemove(bundleId, out _);
        return Task.CompletedTask;
    }

    public Task<int> GetActiveCountAsync(CancellationToken cancellationToken = default)
    {
        var count = _bundles.Values.Count(b =>
            b.Status is BundleStatus.Pending or BundleStatus.InCustody && !b.IsExpired);
        return Task.FromResult(count);
    }

    public Task SaveCustodyAsync(CustodyRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _custody[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CustodyRecord>> GetCustodyRecordsAsync(Guid bundleId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CustodyRecord> records = _custody.Values
            .Where(r => r.BundleId == bundleId)
            .ToArray();
        return Task.FromResult(records);
    }

    public Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default)
    {
        var expired = 0;
        foreach (var kvp in _bundles)
        {
            if (kvp.Value.IsExpired && kvp.Value.Status != BundleStatus.Expired)
            {
                kvp.Value.Status = BundleStatus.Expired;
                expired++;
            }
        }
        return Task.FromResult(expired);
    }
}
