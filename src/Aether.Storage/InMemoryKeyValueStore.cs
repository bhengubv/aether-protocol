// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Aether.Storage;

/// <summary>
/// Process-local, volatile key-value store backed by <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// Suitable for tests and demos. Loses everything on process exit.
/// </summary>
public sealed class InMemoryKeyValueStore : IKeyValueStore
{
    private readonly ConcurrentDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _entries.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    public Task PutAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        // Defensive copy so the caller can't mutate the stored bytes.
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        _entries[key] = copy;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Task.FromResult(_entries.TryRemove(key, out _));
    }

    public Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Task.FromResult(_entries.ContainsKey(key));
    }

    public async IAsyncEnumerable<string> ListKeysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in _entries.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return key;
            await Task.Yield();
        }
    }
}
