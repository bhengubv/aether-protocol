// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherMesh.Security.Services;

/// <summary>
/// Process-local, volatile <see cref="IPreKeyStore"/> backed by ordinary
/// reference fields. Suitable for tests and demos. Loses everything on
/// process exit. Hosts that need persistence wire up the
/// <c>KeyValuePreKeyStore</c> adapter from <c>AetherMesh.Storage</c>.
/// </summary>
public sealed class InMemoryPreKeyStore : IPreKeyStore
{
    private readonly object _lock = new();

    private StoredIdentityKeys? _identity;
    private StoredSignedPreKeyHistory _spkHistory = new(Array.Empty<StoredSignedPreKey>());
    private readonly ConcurrentDictionary<int, StoredOneTimePreKey> _opks = new();

    public Task<StoredIdentityKeys?> LoadIdentityAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_identity);
    }

    public Task SaveIdentityAsync(StoredIdentityKeys identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_lock) _identity = identity;
        return Task.CompletedTask;
    }

    public Task<StoredSignedPreKeyHistory> LoadSignedPreKeysAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_spkHistory);
    }

    public Task SaveSignedPreKeysAsync(StoredSignedPreKeyHistory history, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        lock (_lock) _spkHistory = history;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<int, StoredOneTimePreKey>> LoadOneTimePreKeysAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<int, StoredOneTimePreKey> snapshot = _opks.ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(snapshot);
    }

    public Task SaveOneTimePreKeysAsync(IReadOnlyDictionary<int, StoredOneTimePreKey> pool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _opks.Clear();
        foreach (var (id, opk) in pool)
            _opks[id] = opk;
        return Task.CompletedTask;
    }

    public Task ConsumeOneTimePreKeyAsync(int id, CancellationToken ct = default)
    {
        _opks.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
