// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace AetherNet.Security;

/// <summary>
/// AES-256-GCM envelope — the canonical self-to-self payload format on the
/// Aether mesh. Wire layout: <c>[nonce(12)][tag(16)][cipher(N)]</c>.
///
/// <para>
/// One envelope, one decrypt path. Every protocol-layer producer that needs
/// confidentiality + integrity for a payload addressed to the sender's own
/// future device (scrobbles, bookmark sync, play-history sync, vault-shard
/// metadata, message draft sync) uses this class. The key-management story
/// stays honest because there is exactly one symmetric-key derivation surface
/// and one tag-verification path across the whole protocol.
/// </para>
///
/// <para><b>Format details:</b>
/// <list type="bullet">
///   <item><description>Key: 32 bytes (AES-256).</description></item>
///   <item><description>Nonce: 12 bytes, RNG-generated per call (RFC 5116 §3.2).</description></item>
///   <item><description>Tag: 16 bytes (the GCM authentication tag).</description></item>
///   <item><description>Ciphertext: equal in length to the plaintext.</description></item>
///   <item><description>No associated data (AD) — the envelope is single-purpose;
///     bind context via the key, not via per-envelope AD.</description></item>
/// </list>
/// </para>
///
/// <para><b>Not</b> for inter-peer messaging — those use the Double Ratchet
/// (<see cref="AetherNet.Security"/> Signal Protocol surface). This envelope is
/// strictly for sender→self payloads where there is no recipient public-key
/// negotiation.</para>
/// </summary>
public static class AesGcmEnvelope
{
    /// <summary>Required size of the symmetric key (AES-256 → 32 bytes).</summary>
    public const int KeySize = 32;

    /// <summary>Encrypt <paramref name="plaintext"/> under <paramref name="key"/>.</summary>
    /// <param name="key">A 32-byte AES-256 key.</param>
    /// <param name="plaintext">Bytes to encrypt. May be empty.</param>
    /// <returns>The envelope: <c>[nonce(12)][tag(16)][cipher(N)]</c>.</returns>
    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        ValidateKey(key);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var gcm = new AesGcm(key, tag.Length);
        gcm.Encrypt(nonce, plaintext, cipher, tag);

        var envelope = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce,  0, envelope, 0,                       nonce.Length);
        Buffer.BlockCopy(tag,    0, envelope, nonce.Length,            tag.Length);
        Buffer.BlockCopy(cipher, 0, envelope, nonce.Length + tag.Length, cipher.Length);
        return envelope;
    }

    /// <summary>Decrypt an envelope produced by <see cref="Encrypt"/>.</summary>
    /// <param name="key">A 32-byte AES-256 key — the same key used to encrypt.</param>
    /// <param name="envelope">The encrypted envelope.</param>
    /// <returns>The original plaintext.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException">Key is not 32 bytes.</exception>
    /// <exception cref="FormatException">Envelope is shorter than 28 bytes (nonce + tag).</exception>
    /// <exception cref="AuthenticationTagMismatchException">Tag verification failed — payload tampered or wrong key.</exception>
    public static byte[] Decrypt(byte[] key, byte[] envelope)
    {
        ValidateKey(key);
        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen = AesGcm.TagByteSizes.MaxSize;
        if (envelope is null || envelope.Length < nonceLen + tagLen)
            throw new FormatException("AES-GCM envelope is too short.");

        var nonce = envelope.AsSpan(0, nonceLen);
        var tag = envelope.AsSpan(nonceLen, tagLen);
        var cipher = envelope.AsSpan(nonceLen + tagLen);
        var plaintext = new byte[cipher.Length];
        using var gcm = new AesGcm(key, tagLen);
        gcm.Decrypt(nonce, cipher, tag, plaintext);
        return plaintext;
    }

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes (AES-256).", nameof(key));
    }
}
