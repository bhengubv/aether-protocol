// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherMesh.Security.Services;

/// <summary>
/// Process-local, volatile <see cref="ISignalSessionStore"/> backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. The session bytes are
/// stored as the same JSON envelope a durable store would emit, which both
/// keeps the round-trip path identical to the production code path and
/// makes accidental in-place mutation of stored state impossible.
///
/// Suitable for tests and demos. Loses everything on process exit. Hosts
/// that need persistence wire up the <c>KeyValueSignalSessionStore</c>
/// adapter from <c>AetherMesh.Storage</c> instead.
/// </summary>
internal sealed class InMemorySignalSessionStore : ISignalSessionStore
{
    private readonly ConcurrentDictionary<string, byte[]> _sessions = new(StringComparer.Ordinal);

    public Task<SignalSession?> LoadAsync(string peerUhid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        if (_sessions.TryGetValue(peerUhid, out var bytes))
            return Task.FromResult(SignalSessionSerializer.Deserialize(bytes));
        return Task.FromResult<SignalSession?>(null);
    }

    public Task SaveAsync(string peerUhid, SignalSession session, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(session);
        _sessions[peerUhid] = SignalSessionSerializer.Serialize(session);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string peerUhid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        _sessions.TryRemove(peerUhid, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListPeersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> peers = _sessions.Keys.ToArray();
        return Task.FromResult(peers);
    }
}
