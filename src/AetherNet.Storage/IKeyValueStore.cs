// SPDX-License-Identifier: MIT

namespace AetherNet.Storage;

/// <summary>
/// Generic byte-array-keyed-by-string persistence primitive used as the foundation
/// for every Aether store that needs to survive a process restart. Implementations
/// are responsible for atomicity and durability guarantees; the protocol layer just
/// reads and writes opaque bytes.
///
/// Two reference implementations ship with this project:
/// <see cref="InMemoryKeyValueStore"/> (volatile, process-local) and
/// <see cref="FileSystemKeyValueStore"/> (one file per key, atomic via temp + rename).
/// Hosts that need richer guarantees (transactions, encrypted-at-rest, network-attached)
/// supply their own implementation.
/// </summary>
public interface IKeyValueStore
{
    /// <summary>Returns the bytes stored under <paramref name="key"/>, or null if absent.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the bytes stored under <paramref name="key"/>.</summary>
    Task PutAsync(string key, byte[] value, CancellationToken cancellationToken = default);

    /// <summary>Removes the entry under <paramref name="key"/>, if present. Returns true if a value was removed.</summary>
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Returns true if a value exists under <paramref name="key"/>.</summary>
    Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Enumerates every key currently in the store. The order is implementation-defined.</summary>
    IAsyncEnumerable<string> ListKeysAsync(CancellationToken cancellationToken = default);
}
