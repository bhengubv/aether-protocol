// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AetherMesh.Models;
using AetherMesh.Routing;

namespace AetherMesh.Storage;

/// <summary>
/// <see cref="IRouteStore"/> implementation backed by an arbitrary
/// <see cref="IKeyValueStore"/>. Pair with <see cref="FileSystemKeyValueStore"/>
/// to get persistent routes; pair with <see cref="InMemoryKeyValueStore"/> in tests.
/// </summary>
public sealed class KeyValueRouteStore : IRouteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IKeyValueStore _kv;

    public KeyValueRouteStore(IKeyValueStore kv)
    {
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
    }

    public async Task<RouteEntry?> GetAsync(string destinationUhid, CancellationToken cancellationToken = default)
    {
        var bytes = await _kv.GetAsync(Key(destinationUhid), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : JsonSerializer.Deserialize<RouteEntry>(bytes, JsonOptions);
    }

    public async Task<IReadOnlyList<RouteEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var routes = new List<RouteEntry>();
        await foreach (var key in _kv.ListKeysAsync(cancellationToken).ConfigureAwait(false))
        {
            var bytes = await _kv.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;
            var route = JsonSerializer.Deserialize<RouteEntry>(bytes, JsonOptions);
            if (route is not null) routes.Add(route);
        }
        return routes;
    }

    public Task SaveAsync(RouteEntry route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(route, JsonOptions);
        return _kv.PutAsync(Key(route.DestinationUhid), bytes, cancellationToken);
    }

    public async Task RemoveAsync(string destinationUhid, CancellationToken cancellationToken = default)
    {
        await _kv.RemoveAsync(Key(destinationUhid), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        var pruned = 0;
        await foreach (var key in _kv.ListKeysAsync(cancellationToken).ConfigureAwait(false))
        {
            var bytes = await _kv.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;
            var route = JsonSerializer.Deserialize<RouteEntry>(bytes, JsonOptions);
            if (route is not null && route.IsExpired)
            {
                if (await _kv.RemoveAsync(key, cancellationToken).ConfigureAwait(false))
                    pruned++;
            }
        }
        return pruned;
    }

    private static string Key(string destinationUhid) => "route:" + destinationUhid;
}
