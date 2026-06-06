// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Storage;

/// <summary>
/// <see cref="IDataAtRestKeyProvider"/> that derives a 32-byte AES-256 key
/// from a passphrase and a salt using PBKDF2-HMAC-SHA256. The derived key
/// is cached for the lifetime of the provider so the (relatively expensive)
/// PBKDF2 computation runs exactly once per passphrase/version pair.
///
/// <para>
/// <b>Production iteration count: 600,000.</b> This matches the OWASP 2023
/// recommendation for PBKDF2-HMAC-SHA256 and is the default if no count is
/// supplied. Tests pass a smaller count to keep the suite fast — never lower
/// the default in production code.
/// </para>
///
/// <para>
/// The salt is required, must be at least 16 bytes, and MUST be unique to
/// this device (or this trust boundary). Reusing the same passphrase + salt
/// across devices would let an attacker who recovered the salt from one
/// device decrypt blobs from another — domain-separate by appending an
/// install-id, hardware-id, or randomly generated per-device value.
/// </para>
///
/// <para>
/// To rotate the key, construct a new provider with both the old and new
/// passphrases supplied via <see cref="WithRotation"/> — the old version
/// keeps decrypting historical blobs while new writes use the new key.
/// </para>
/// </summary>
public sealed class DerivedDataAtRestKeyProvider : IDataAtRestKeyProvider
{
    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    private const int KeyByteLength = 32; // AES-256
    private const int MinimumSaltLength = 16;

    private readonly Dictionary<int, byte[]> _derivedKeys;

    /// <summary>
    /// Construct a single-version provider that derives version 1 from the
    /// supplied <paramref name="passphrase"/> and <paramref name="salt"/>.
    /// </summary>
    /// <param name="passphrase">The user/host passphrase. UTF-8 encoded before derivation.</param>
    /// <param name="salt">At least 16 bytes; should be unique per device.</param>
    /// <param name="iterations">
    /// PBKDF2 iteration count. Defaults to <see cref="DefaultIterations"/>
    /// (the OWASP 2023 recommendation). Tests may pass a smaller value.
    /// </param>
    public DerivedDataAtRestKeyProvider(
        string passphrase,
        byte[] salt,
        int iterations = DefaultIterations)
    {
        ValidateInputs(passphrase, salt, iterations);

        _derivedKeys = new Dictionary<int, byte[]>
        {
            [1] = Derive(passphrase, salt, iterations),
        };
        CurrentVersion = 1;
        Iterations = iterations;
    }

    private DerivedDataAtRestKeyProvider(
        Dictionary<int, byte[]> derivedKeys,
        int currentVersion,
        int iterations)
    {
        _derivedKeys = derivedKeys;
        CurrentVersion = currentVersion;
        Iterations = iterations;
    }

    /// <inheritdoc />
    public int CurrentVersion { get; }

    /// <summary>The PBKDF2 iteration count this provider was constructed with.</summary>
    public int Iterations { get; }

    /// <inheritdoc />
    public byte[]? GetKey(int version)
    {
        return _derivedKeys.TryGetValue(version, out var key) ? key : null;
    }

    /// <summary>
    /// Returns a new provider that adds a freshly derived key under
    /// <paramref name="newVersion"/> (which becomes <see cref="CurrentVersion"/>)
    /// while keeping every existing version available for decryption. Use
    /// during a rotation window: hosts swap the registered provider, run
    /// <see cref="EncryptedKeyValueStore.RewrapAsync"/> across the store
    /// in the background, then drop the old key on the next deploy by
    /// constructing a single-version provider on the new passphrase.
    /// </summary>
    public DerivedDataAtRestKeyProvider WithRotation(
        int newVersion,
        string newPassphrase,
        byte[] newSalt,
        int? iterations = null)
    {
        if (newVersion < 1 || newVersion > 255)
            throw new ArgumentOutOfRangeException(nameof(newVersion),
                "Key version must fit in a single byte (1..255).");
        if (_derivedKeys.ContainsKey(newVersion))
            throw new ArgumentException(
                $"Version {newVersion} already exists in this provider.",
                nameof(newVersion));

        var iters = iterations ?? Iterations;
        ValidateInputs(newPassphrase, newSalt, iters);

        var next = new Dictionary<int, byte[]>(_derivedKeys.Count + 1);
        foreach (var kv in _derivedKeys) next[kv.Key] = kv.Value;
        next[newVersion] = Derive(newPassphrase, newSalt, iters);

        return new DerivedDataAtRestKeyProvider(next, newVersion, iters);
    }

    private static void ValidateInputs(string passphrase, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length < MinimumSaltLength)
            throw new ArgumentException(
                $"Salt must be at least {MinimumSaltLength} bytes.",
                nameof(salt));
        if (iterations < 1)
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                "Iteration count must be positive.");
    }

    private static byte[] Derive(string passphrase, byte[] salt, int iterations)
    {
        // Defensive copy of salt before derivation so caller mutations don't
        // affect the cached key.
        var saltCopy = new byte[salt.Length];
        Buffer.BlockCopy(salt, 0, saltCopy, 0, salt.Length);

        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passphraseBytes,
                saltCopy,
                iterations,
                HashAlgorithmName.SHA256,
                KeyByteLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }
}
