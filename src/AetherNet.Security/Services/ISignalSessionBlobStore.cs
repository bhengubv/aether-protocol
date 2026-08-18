// SPDX-License-Identifier: MIT

namespace AetherNet.Security.Services;

/// <summary>
/// Durable storage for Signal session state, as opaque bytes.
///
/// <para>
/// This is the seam a host implements to make sessions survive a restart. It deals in blobs rather
/// than in session objects deliberately: the ratchet's internals stay inside this assembly, the wire
/// format stays the library's business, and the host only has to answer "put these bytes somewhere I
/// can get them back". A host that wanted the session type would be a host that could corrupt it.
/// </para>
///
/// <para>
/// <b>Why this exists.</b> Without a store, sessions live only in memory, and every launch starts with
/// amnesia. Two phones then rebuild their session independently — each as X3DH <i>initiator</i> — and
/// end up with two different root keys for one pair, so every message between them fails its
/// authentication tag. It is not subtle and it is not recoverable by retrying: it looks like broken
/// crypto, and it cost a full day of on-device debugging before anyone checked whether sessions were
/// being persisted at all. Persistence had been written and tested; it simply had no public way to be
/// switched on.
/// </para>
///
/// <para>
/// <b>What the blob contains.</b> Root key, chain keys, ratchet private key and skipped message keys —
/// everything needed to read the conversation. It must be stored where other apps cannot read it, and
/// ideally encrypted at rest. Anything world-readable makes the ratchet pointless.
/// </para>
/// </summary>
public interface ISignalSessionBlobStore
{
    /// <summary>The stored blob for this peer, or null if there is none.</summary>
    Task<byte[]?> LoadAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store the blob for this peer, replacing anything already there. Called on every ratchet
    /// mutation, so it should be cheap — a local write, never a network round trip.
    /// </summary>
    Task SaveAsync(string peerUhid, byte[] blob, CancellationToken cancellationToken = default);

    /// <summary>Forget this peer's session. Called when a session is dropped as unusable.</summary>
    Task DeleteAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>Every peer with a stored session, so they can be rehydrated on startup.</summary>
    Task<IReadOnlyList<string>> ListPeersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts a host's <see cref="ISignalSessionBlobStore"/> to the internal store the ratchet uses,
/// owning the serialisation so the host never sees a session.
/// </summary>
internal sealed class BlobBackedSignalSessionStore : ISignalSessionStore
{
    private readonly ISignalSessionBlobStore _blobs;

    public BlobBackedSignalSessionStore(ISignalSessionBlobStore blobs)
    {
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
    }

    public async Task<SignalSession?> LoadAsync(string peerUhid, CancellationToken ct = default)
    {
        var blob = await _blobs.LoadAsync(peerUhid, ct).ConfigureAwait(false);

        // A blob that will not deserialise is treated as no session rather than an error. It means the
        // stored format predates this build, or the bytes were damaged; either way the right answer is
        // to establish a fresh session, not to refuse to start.
        return blob is null or { Length: 0 } ? null : SignalSessionSerializer.Deserialize(blob);
    }

    public Task SaveAsync(string peerUhid, SignalSession session, CancellationToken ct = default) =>
        _blobs.SaveAsync(peerUhid, SignalSessionSerializer.Serialize(session), ct);

    public Task DeleteAsync(string peerUhid, CancellationToken ct = default) =>
        _blobs.DeleteAsync(peerUhid, ct);

    public Task<IReadOnlyList<string>> ListPeersAsync(CancellationToken ct = default) =>
        _blobs.ListPeersAsync(ct);
}
