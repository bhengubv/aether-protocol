// SPDX-License-Identifier: MIT

namespace AetherNet.Voice;

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
    private static int _warned;

    private static void WarnOnce()
    {
        // Fire exactly once per process so a host that forgot to wire a real provider gets a
        // visible signal that group voice is NOT encrypted — the interface doc promises this
        // warning, and without it identity "encryption" ships silently as plaintext.
        if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
        {
            System.Diagnostics.Trace.TraceWarning(
                "AetherNet: NullGroupKeyProvider is active — group voice frames are NOT encrypted " +
                "(identity 'encryption' over a constant zero key). This is for tests only; wire a " +
                "real IGroupKeyProvider backed by your security service in production.");
        }
    }

    public byte[] GenerateSenderKey() { WarnOnce(); return new byte[32]; }
    public Task<byte[]> WrapForAsync(string recipientUhid, byte[] senderKey, CancellationToken cancellationToken = default)
    { WarnOnce(); return Task.FromResult(senderKey); }
    public Task<byte[]?> UnwrapAsync(string senderUhid, byte[] wrappedKey, CancellationToken cancellationToken = default)
    { WarnOnce(); return Task.FromResult<byte[]?>(wrappedKey); }
    public byte[] EncryptFrame(byte[] senderKey, ReadOnlySpan<byte> plaintext) { WarnOnce(); return plaintext.ToArray(); }
    public byte[]? DecryptFrame(byte[] senderKey, ReadOnlySpan<byte> ciphertext) { WarnOnce(); return ciphertext.ToArray(); }
}
