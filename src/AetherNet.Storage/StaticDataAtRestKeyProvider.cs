// SPDX-License-Identifier: MIT

namespace AetherNet.Storage;

/// <summary>
/// Simple <see cref="IDataAtRestKeyProvider"/> backed by one or more
/// pre-derived 32-byte AES-256 keys that the host supplies directly.
/// Useful for tests, demos, and deployments that derive their key material
/// out of band (e.g. from the OS keychain, a hardware enclave, or a remote
/// KMS) and just need to inject the resulting bytes into the wrapper.
///
/// <para>
/// The simplest construction takes a single 32-byte key and assigns it
/// version 1 — sufficient for hosts that never rotate. Hosts that rotate
/// pass the dictionary constructor with both the previous and current
/// versions so that values written under the old key keep decrypting
/// during the rotation window.
/// </para>
/// </summary>
public sealed class StaticDataAtRestKeyProvider : IDataAtRestKeyProvider
{
    private readonly Dictionary<int, byte[]> _keys;

    /// <summary>
    /// Create a single-version provider where <paramref name="key"/> is the
    /// AES-256 master key and <see cref="CurrentVersion"/> defaults to 1.
    /// </summary>
    /// <param name="key">A 32-byte AES-256 key.</param>
    public StaticDataAtRestKeyProvider(byte[] key)
        : this(new Dictionary<int, byte[]>
        {
            [1] = ValidateKey(key),
        }, currentVersion: 1)
    {
    }

    /// <summary>
    /// Create a multi-version provider for key-rotation deployments. Every
    /// value in <paramref name="keysByVersion"/> must be 32 bytes;
    /// <paramref name="currentVersion"/> must reference a key that is
    /// present in the dictionary and must be in the range [1, 255].
    /// </summary>
    public StaticDataAtRestKeyProvider(
        IReadOnlyDictionary<int, byte[]> keysByVersion,
        int currentVersion)
    {
        ArgumentNullException.ThrowIfNull(keysByVersion);

        if (currentVersion < 1 || currentVersion > 255)
            throw new ArgumentOutOfRangeException(
                nameof(currentVersion),
                "Key version must fit in a single byte (1..255).");

        if (!keysByVersion.ContainsKey(currentVersion))
            throw new ArgumentException(
                $"keysByVersion does not contain an entry for currentVersion={currentVersion}.",
                nameof(keysByVersion));

        _keys = new Dictionary<int, byte[]>(keysByVersion.Count);
        foreach (var kv in keysByVersion)
        {
            if (kv.Key < 1 || kv.Key > 255)
                throw new ArgumentException(
                    $"Key version {kv.Key} is outside the supported [1, 255] range.",
                    nameof(keysByVersion));
            _keys[kv.Key] = ValidateKey(kv.Value);
        }

        CurrentVersion = currentVersion;
    }

    /// <inheritdoc />
    public int CurrentVersion { get; }

    /// <inheritdoc />
    public byte[]? GetKey(int version)
    {
        return _keys.TryGetValue(version, out var key) ? key : null;
    }

    private static byte[] ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new ArgumentException(
                "Data-at-rest key must be exactly 32 bytes (AES-256).",
                nameof(key));
        // Defensive copy — caller can't subsequently zero our key buffer.
        var copy = new byte[key.Length];
        Buffer.BlockCopy(key, 0, copy, 0, key.Length);
        return copy;
    }
}
