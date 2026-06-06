// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Storage;

/// <summary>
/// Transparent encryption-at-rest wrapper for an arbitrary <see cref="IKeyValueStore"/>.
/// Encrypts every value on the way down and decrypts on the way up using AES-256-GCM
/// with a per-write random nonce. Keys are passed through unchanged so list/range
/// queries continue to work.
///
/// <para>
/// <b>Threat model:</b> protects persisted bytes from an attacker who recovers the
/// underlying medium (stolen disk, recycled SD card, leaked backup) without
/// compromising the master-key material that the host hands to the
/// <see cref="IDataAtRestKeyProvider"/>. The wrapper does NOT hide write
/// patterns, key names, or value sizes. It does NOT defend against in-process
/// memory disclosure — values are plaintext while the application holds them.
/// </para>
///
/// <para>
/// <b>Wire format (per stored blob):</b>
/// </para>
/// <code>
/// keyVersion (1 byte) || nonce (12 bytes) || ciphertext (N bytes) || tag (16 bytes)
/// </code>
/// <para>
/// The <c>keyVersion</c> byte names which key in the provider was used; the wrapper
/// looks it up on read, so hosts can run a rotation window with both old and new keys
/// loaded. Tampering with any byte fails GCM authentication and the read returns null
/// (treated as "not present" by callers).
/// </para>
///
/// <para>
/// <b>Composition:</b> existing adapters (<see cref="KeyValueRouteStore"/>,
/// <see cref="KeyValueDtnBundleStore"/>, <see cref="KeyValueMessageStore"/>,
/// <see cref="KeyValueSignalSessionStore"/>, <see cref="KeyValuePreKeyStore"/>) consume
/// any <see cref="IKeyValueStore"/>, so wrapping is a one-line composition:
/// </para>
/// <code>
/// IKeyValueStore inner = new FileSystemKeyValueStore(rootDir);
/// IKeyValueStore secure = new EncryptedKeyValueStore(inner, keyProvider);
/// var routes = new KeyValueRouteStore(secure);
/// </code>
/// </summary>
public sealed class EncryptedKeyValueStore : IKeyValueStore
{
    /// <summary>AES-256 key length in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>AES-GCM nonce length in bytes.</summary>
    public const int NonceSize = 12;

    /// <summary>AES-GCM authentication tag length in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>Length of the version-byte header at the start of every blob.</summary>
    public const int VersionHeaderSize = 1;

    /// <summary>Minimum byte count for any well-formed encrypted blob.</summary>
    public const int MinimumBlobSize = VersionHeaderSize + NonceSize + TagSize;

    private readonly IKeyValueStore _inner;
    private readonly IDataAtRestKeyProvider _keyProvider;
    private readonly ILogger<EncryptedKeyValueStore> _logger;

    /// <summary>
    /// Wrap <paramref name="inner"/> with transparent AES-256-GCM encryption.
    /// </summary>
    /// <param name="inner">The underlying KV store that holds encrypted bytes.</param>
    /// <param name="keyProvider">Supplies the master key(s) and current version.</param>
    /// <param name="logger">Optional logger. Tamper events log at warning level.</param>
    public EncryptedKeyValueStore(
        IKeyValueStore inner,
        IDataAtRestKeyProvider keyProvider,
        ILogger<EncryptedKeyValueStore>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _logger = logger ?? NullLogger<EncryptedKeyValueStore>.Instance;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var blob = await _inner.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (blob is null) return null;

        if (blob.Length < MinimumBlobSize)
        {
            _logger.LogWarning(
                "Encrypted blob under key='{Key}' is smaller than the minimum {Min} bytes — treating as tampered/missing.",
                key, MinimumBlobSize);
            return null;
        }

        var version = blob[0];
        var keyBytes = _keyProvider.GetKey(version);
        if (keyBytes is null)
        {
            _logger.LogWarning(
                "No data-at-rest key registered for version={Version} under key='{Key}' — cannot decrypt.",
                version, key);
            return null;
        }

        var nonce = new ReadOnlySpan<byte>(blob, VersionHeaderSize, NonceSize);
        var ciphertextLength = blob.Length - VersionHeaderSize - NonceSize - TagSize;
        var ciphertext = new ReadOnlySpan<byte>(blob, VersionHeaderSize + NonceSize, ciphertextLength);
        var tag = new ReadOnlySpan<byte>(blob, blob.Length - TagSize, TagSize);

        var plaintext = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(keyBytes, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            // GCM authentication failed: caller is treating the value as absent
            // rather than raising — mirrors the messaging-layer behaviour.
            _logger.LogWarning(
                ex,
                "AES-GCM authentication failed reading key='{Key}' (version={Version}). " +
                "Either the wrong key is configured or the blob has been tampered with.",
                key, version);
            return null;
        }
    }

    /// <inheritdoc />
    public Task PutAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        var version = _keyProvider.CurrentVersion;
        if (version < 1 || version > 255)
            throw new InvalidOperationException(
                $"IDataAtRestKeyProvider.CurrentVersion={version} is outside the supported [1, 255] range.");

        var keyBytes = _keyProvider.GetKey(version)
            ?? throw new InvalidOperationException(
                $"IDataAtRestKeyProvider returned null for its own CurrentVersion={version}.");

        if (keyBytes.Length != KeySize)
            throw new InvalidOperationException(
                $"IDataAtRestKeyProvider returned a {keyBytes.Length}-byte key; AES-256 requires {KeySize} bytes.");

        var blob = new byte[VersionHeaderSize + NonceSize + value.Length + TagSize];
        blob[0] = (byte)version;

        var nonceSpan = blob.AsSpan(VersionHeaderSize, NonceSize);
        RandomNumberGenerator.Fill(nonceSpan);

        var ciphertextSpan = blob.AsSpan(VersionHeaderSize + NonceSize, value.Length);
        var tagSpan = blob.AsSpan(blob.Length - TagSize, TagSize);

        using var aes = new AesGcm(keyBytes, TagSize);
        aes.Encrypt(nonceSpan, value, ciphertextSpan, tagSpan);

        return _inner.PutAsync(key, blob, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _inner.RemoveAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _inner.ContainsAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListKeysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var k in _inner.ListKeysAsync(cancellationToken).ConfigureAwait(false))
            yield return k;
    }

    /// <summary>
    /// Re-encrypts every value in the underlying store under the provider's
    /// current key version. Use during a key-rotation window after the
    /// provider has been swapped out for one that holds both the old and new
    /// keys — values written under the old version stay readable, and after
    /// the rewrap completes every blob is on the new version so the host can
    /// retire the old key on the next deploy.
    /// </summary>
    /// <returns>The number of values successfully rewrapped.</returns>
    public async Task<int> RewrapAsync(CancellationToken cancellationToken = default)
    {
        var rewrapped = 0;
        var keys = new List<string>();
        await foreach (var k in _inner.ListKeysAsync(cancellationToken).ConfigureAwait(false))
            keys.Add(k);

        foreach (var k in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plaintext = await GetAsync(k, cancellationToken).ConfigureAwait(false);
            if (plaintext is null)
            {
                _logger.LogWarning(
                    "Skipping rewrap of key='{Key}' — value could not be decrypted under any registered key version.",
                    k);
                continue;
            }
            await PutAsync(k, plaintext, cancellationToken).ConfigureAwait(false);
            rewrapped++;
        }
        return rewrapped;
    }
}
