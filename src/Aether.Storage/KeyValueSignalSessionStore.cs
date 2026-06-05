// SPDX-License-Identifier: MIT

using AetherMesh.Security.Services;

namespace AetherMesh.Storage;

/// <summary>
/// <see cref="ISignalSessionStore"/> implementation backed by an arbitrary
/// <see cref="IKeyValueStore"/>. Sessions are JSON-encoded under
/// <c>signal:session:&lt;peerUhid&gt;</c>.
///
/// Implementing the <see cref="ISignalSessionStore"/> interface requires
/// access to the internal <c>SignalSession</c> type — granted via
/// <c>InternalsVisibleTo("AetherMesh.Storage")</c> on <c>AetherMesh.Security</c>.
/// Hosts that want a different on-disk format (encrypted-at-rest, sqlite,
/// etc.) ship their own adapter against <c>IKeyValueStore</c> or against
/// <c>ISignalSessionStore</c> directly.
/// </summary>
internal sealed class KeyValueSignalSessionStore : ISignalSessionStore
{
    private const string Prefix = "signal:session:";
    private readonly IKeyValueStore _kv;

    public KeyValueSignalSessionStore(IKeyValueStore kv)
    {
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
    }

    public async Task<SignalSession?> LoadAsync(string peerUhid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        var bytes = await _kv.GetAsync(Key(peerUhid), ct).ConfigureAwait(false);
        return bytes is null ? null : SignalSessionSerializer.Deserialize(bytes);
    }

    public Task SaveAsync(string peerUhid, SignalSession session, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(session);
        var bytes = SignalSessionSerializer.Serialize(session);
        return _kv.PutAsync(Key(peerUhid), bytes, ct);
    }

    public async Task DeleteAsync(string peerUhid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        await _kv.RemoveAsync(Key(peerUhid), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListPeersAsync(CancellationToken ct = default)
    {
        var peers = new List<string>();
        await foreach (var key in _kv.ListKeysAsync(ct).ConfigureAwait(false))
        {
            if (!key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            peers.Add(key.Substring(Prefix.Length));
        }
        return peers;
    }

    private static string Key(string peerUhid) => Prefix + peerUhid;
}
