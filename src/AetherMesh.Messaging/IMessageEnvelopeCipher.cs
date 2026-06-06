// SPDX-License-Identifier: MIT

namespace AetherMesh.Messaging;

/// <summary>
/// Encrypts plaintext message bodies for a specific recipient and decrypts incoming
/// ciphertext from a specific sender. The default implementation is a stub that
/// returns null on every call — meaning "no Signal session yet, queue this message".
///
/// Hosts that want real end-to-end encryption supply an implementation backed by
/// the Signal Protocol (typically from <c>AetherMesh.Security</c>'s
/// <c>ISignalProtocolService</c>). The messaging layer never sees plaintext keys —
/// it only ever sees the ciphertext byte arrays returned here.
///
/// **Security rule** (from the private CircleAether implementation): if
/// <see cref="EncryptAsync"/> returns null, the caller MUST NOT fall back to any
/// weaker scheme. The message is queued and re-attempted once a session exists.
/// Messages without a Signal session are queued, never sent insecurely.
/// </summary>
public interface IMessageEnvelopeCipher
{
    /// <summary>
    /// Returns the ciphertext for <paramref name="plaintext"/> addressed to
    /// <paramref name="recipientUhid"/>, or null if no session exists yet.
    /// Returning null causes the message to be queued in the outbox.
    /// </summary>
    Task<byte[]?> EncryptAsync(string recipientUhid, byte[] plaintext, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    /// <summary>
    /// Returns the plaintext for <paramref name="ciphertext"/> coming from
    /// <paramref name="senderUhid"/>, or null if the session is missing or the
    /// ciphertext failed to decrypt. Returning null causes the message to be
    /// dropped (it will not be surfaced to the application).
    /// </summary>
    Task<byte[]?> DecryptAsync(string senderUhid, byte[] ciphertext, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    /// <summary>
    /// True if there is currently an active session with <paramref name="peerUhid"/>.
    /// The default returns false; real implementations should consult their
    /// session store. Used by the messaging service to decide whether to attempt
    /// to send or queue.
    /// </summary>
    bool HasSession(string peerUhid) => false;
}

/// <summary>
/// Default permissive stub. Always returns null — i.e. "no session available, queue everything".
/// Suitable only for tests where messages aren't actually sent over the wire.
/// </summary>
public sealed class NullMessageEnvelopeCipher : IMessageEnvelopeCipher
{
}
