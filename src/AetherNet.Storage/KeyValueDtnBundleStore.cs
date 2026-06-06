// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Dtn;
using AetherNet.Models;

namespace AetherNet.Storage;

/// <summary>
/// <see cref="IDtnBundleStore"/> implementation backed by an arbitrary <see cref="IKeyValueStore"/>.
/// Bundles are JSON-encoded under <c>bundle:&lt;guid&gt;</c>, custody records under
/// <c>custody:&lt;guid&gt;</c>. List-style queries scan the keyspace; this is fine for
/// the bounded numbers DTN allows (<see cref="AetherNet.Constants.ProtocolConstants.DtnMaxBundlesPerNode"/>)
/// but a host with millions of bundles should ship a custom store with proper indexes.
/// </summary>
public sealed class KeyValueDtnBundleStore : IDtnBundleStore
{
    private const string BundlePrefix = "bundle:";
    private const string CustodyPrefix = "custody:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IKeyValueStore _kv;

    public KeyValueDtnBundleStore(IKeyValueStore kv)
    {
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
    }

    public async Task<DtnBundle?> GetAsync(Guid bundleId, CancellationToken cancellationToken = default)
    {
        var bytes = await _kv.GetAsync(BundleKey(bundleId), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : JsonSerializer.Deserialize<DtnBundle>(bytes, JsonOptions);
    }

    public async Task<IReadOnlyList<DtnBundle>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var active = new List<DtnBundle>();
        await foreach (var key in _kv.ListKeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!key.StartsWith(BundlePrefix, StringComparison.Ordinal)) continue;
            var bytes = await _kv.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;
            var bundle = JsonSerializer.Deserialize<DtnBundle>(bytes, JsonOptions);
            if (bundle is not null
                && (bundle.Status is BundleStatus.Pending or BundleStatus.InCustody)
                && !bundle.IsExpired)
            {
                active.Add(bundle);
            }
        }
        return active;
    }

    public Task SaveAsync(DtnBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOptions);
        return _kv.PutAsync(BundleKey(bundle.Id), bytes, cancellationToken);
    }

    public async Task RemoveAsync(Guid bundleId, CancellationToken cancellationToken = default)
    {
        await _kv.RemoveAsync(BundleKey(bundleId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> GetActiveCountAsync(CancellationToken cancellationToken = default)
    {
        var active = await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return active.Count;
    }

    public Task SaveCustodyAsync(CustodyRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        return _kv.PutAsync(CustodyKey(record.Id), bytes, cancellationToken);
    }

    public async Task<IReadOnlyList<CustodyRecord>> GetCustodyRecordsAsync(Guid bundleId, CancellationToken cancellationToken = default)
    {
        var records = new List<CustodyRecord>();
        await foreach (var key in _kv.ListKeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!key.StartsWith(CustodyPrefix, StringComparison.Ordinal)) continue;
            var bytes = await _kv.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;
            var record = JsonSerializer.Deserialize<CustodyRecord>(bytes, JsonOptions);
            if (record is not null && record.BundleId == bundleId)
                records.Add(record);
        }
        return records;
    }

    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default)
    {
        var expired = 0;
        await foreach (var key in _kv.ListKeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!key.StartsWith(BundlePrefix, StringComparison.Ordinal)) continue;
            var bytes = await _kv.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;
            var bundle = JsonSerializer.Deserialize<DtnBundle>(bytes, JsonOptions);
            if (bundle is not null && bundle.IsExpired && bundle.Status != BundleStatus.Expired)
            {
                bundle.Status = BundleStatus.Expired;
                await SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
                expired++;
            }
        }
        return expired;
    }

    private static string BundleKey(Guid id) => BundlePrefix + id.ToString("N");
    private static string CustodyKey(Guid id) => CustodyPrefix + id.ToString("N");
}
