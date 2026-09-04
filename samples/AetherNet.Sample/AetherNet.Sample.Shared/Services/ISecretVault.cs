// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Where this device keeps a secret that must never leave it — today, the Ed25519 private key behind
/// your AetherTag. Platform-specific by design: on a phone this is hardware-backed (Android Keystore),
/// elsewhere it falls back to an encrypted file.
/// </summary>
public interface ISecretVault
{
    /// <summary>True when the secret is sealed by hardware the OS won't export (a real phone).</summary>
    bool IsHardwareBacked { get; }

    /// <summary>A short, honest description of how the secret is protected, for the UI to show.</summary>
    string ProtectionDescription { get; }

    /// <summary>
    /// The secret, or null if this device has never stored one.
    /// <para>
    /// Throws <see cref="SecretUnavailableException"/> when a secret <b>is</b> stored but cannot be
    /// opened right now — a locked phone, most often. Null and "not now" must never be confused: a
    /// caller that reads null will create a replacement, and for an identity key that is destruction,
    /// not recovery.
    /// </para>
    /// </summary>
    byte[]? Get(string name);

    /// <summary>Is something stored under this name, whether or not it can be opened right now?</summary>
    bool Has(string name);

    void Set(string name, byte[] secret);

    /// <summary>
    /// Destroy the secret stored under this name — the panic-wipe primitive. Best-effort and
    /// idempotent: a name that was never stored is a no-op, and after this <see cref="Has"/> is false.
    /// The app owns this: <c>PanicWipe</c> gives the manifest of key-store names, the vault removes them.
    /// </summary>
    void Remove(string name);
}

/// <summary>
/// A secret exists but cannot be read at this moment. Always temporary — try again once the phone is
/// unlocked. Never a reason to make a new one.
/// </summary>
public sealed class SecretUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Fallback vault for hosts without a hardware keystore (the Web head, desktop). The secret is
/// AES-256-GCM encrypted with a key held in a sibling file.
///
/// Honest limit: this protects against casual copying of the data file, NOT against anyone who can
/// already read the process's own directory. On a phone the Android Keystore implementation is the
/// real one — this exists so the Web/desktop head runs, not because it is equivalent.
/// </summary>
public sealed class FileSecretVault : ISecretVault
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _directory;
    private readonly byte[] _key;

    public FileSecretVault(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
        _key = LoadOrCreateKey(Path.Combine(_directory, "vault.key"));
    }

    public bool IsHardwareBacked => false;

    public string ProtectionDescription => "Encrypted on this device";

    public byte[]? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;

        var blob = File.ReadAllBytes(path);
        if (blob.Length < NonceSize + TagSize) return null;

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (CryptographicException ex)
        {
            // The file is there, so a secret was stored; we just cannot open it. Saying "absent" here
            // would invite the caller to overwrite it with a new one.
            throw new SecretUnavailableException("The stored secret could not be decrypted.", ex);
        }
    }

    /// <inheritdoc />
    public bool Has(string name) => File.Exists(PathFor(name));

    public void Set(string name, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[secret.Length];

        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, secret, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob.AsSpan(0));
        tag.CopyTo(blob.AsSpan(NonceSize));
        cipher.CopyTo(blob.AsSpan(NonceSize + TagSize));

        WriteAtomic(PathFor(name), blob);
    }

    /// <inheritdoc />
    public void Remove(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return;

        // Overwrite the sealed bytes before unlinking — defence in depth, so the ciphertext is not
        // merely dereferenced and left on disk for a file-recovery tool to lift.
        try
        {
            var len = (int)new FileInfo(path).Length;
            if (len > 0) WriteAtomic(path, RandomNumberGenerator.GetBytes(len));
        }
        catch { /* best-effort scrub; the delete below is what matters */ }

        try { File.Delete(path); } catch { /* the wipe carries on regardless of one stubborn file */ }
    }

    private string PathFor(string name) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))) + ".sealed");

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == KeySize) return existing;
        }
        var key = RandomNumberGenerator.GetBytes(KeySize);
        WriteAtomic(path, key);
        return key;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
