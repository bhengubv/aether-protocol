// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Security.Services;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Keeps Signal sessions in the device database, so a conversation survives the app closing.
///
/// <para>
/// Until this existed, sessions lived only in memory. Every launch began with amnesia: both phones
/// would rebuild independently, each as X3DH initiator, and end up holding different root keys for the
/// same pair — after which every message between them failed its authentication tag. Chat appeared to
/// cope only because it repairs on a decrypt failure and re-establishes; a call had no such path, so it
/// simply never connected. Days were spent on the symptoms.
/// </para>
///
/// <para>
/// The blobs go in <see cref="AetherStore"/> alongside the messages they protect. That is deliberate:
/// this database is app-private, so a session is exactly as well protected as the conversation it can
/// decrypt — no better, and no worse, which is an honest place to draw the line. Nothing here is
/// world-readable and nothing leaves the device.
/// </para>
/// </summary>
public sealed class StoredSignalSessions : ISignalSessionBlobStore
{
    private readonly AetherStore _store;

    public StoredSignalSessions(AetherStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<byte[]?> LoadAsync(string peerUhid, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetSessionBlob(peerUhid));

    public Task SaveAsync(string peerUhid, byte[] blob, CancellationToken cancellationToken = default)
    {
        _store.SaveSessionBlob(peerUhid, blob);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        _store.DeleteSessionBlob(peerUhid);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListPeersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.GetSessionPeers());
}
