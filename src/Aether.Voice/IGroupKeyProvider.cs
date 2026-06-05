// SPDX-License-Identifier: MIT

namespace AetherMesh.Voice;

/// <summary>
/// Sender-key seam for group voice. The host generates a fresh group key on every
/// membership change and wraps it once per participant (typically using each
/// participant's Signal session); each participant unwraps it locally before
/// decrypting subsequent frames.
///
/// The default <see cref="NullGroupKeyProvider"/> ships a constant zero key and
/// emits a warning — useful for tests where ciphertext-vs-plaintext doesn't matter.
/// Production hosts wire up an implementation backed by their security service.
/// </summary>
public interface IGroupKeyProvider
{
    /// <summary>Generate a fresh sender-key for a new key generation. Implementations should produce 32 cryptographically random bytes.</summary>
    byte[] GenerateSenderKey();

    /// <summary>
    /// Wrap <paramref name="senderKey"/> for delivery to <paramref name="recipientUhid"/> so
    /// only that recipient can unwrap it (typically using the existing pairwise Signal session).
    /// Returning empty bytes means "no session yet" — the caller will queue / retry.
    /// </summary>
    Task<byte[]> WrapForAsync(string recipientUhid, byte[] senderKey, CancellationToken cancellationToken = default);

    /// <summary>Unwrap a sender-key blob received from <paramref name="senderUhid"/>.</summary>
    Task<byte[]?> UnwrapAsync(string senderUhid, byte[] wrappedKey, CancellationToken cancellationToken = default);

    /// <summary>Encrypt a single voice frame with the given sender-key.</summary>
    byte[] EncryptFrame(byte[] senderKey, ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypt a single voice frame with the given sender-key.</summary>
    byte[]? DecryptFrame(byte[] senderKey, ReadOnlySpan<byte> ciphertext);
}

/// <summary>
/// Null provider — emits a 32-byte zero key and an identity "encryption". Tests only.
/// </summary>
public sealed class NullGroupKeyProvider : IGroupKeyProvider
{
    public byte[] GenerateSenderKey() => new byte[32];
    public Task<byte[]> WrapForAsync(string recipientUhid, byte[] senderKey, CancellationToken cancellationToken = default)
        => Task.FromResult(senderKey);
    public Task<byte[]?> UnwrapAsync(string senderUhid, byte[] wrappedKey, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(wrappedKey);
    public byte[] EncryptFrame(byte[] senderKey, ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
    public byte[]? DecryptFrame(byte[] senderKey, ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();
}
