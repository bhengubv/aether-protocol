// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Security.Privacy;

/// <summary>
/// Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.
/// A duress PIN (or panic button) irreversibly destroys the node's key material,
/// so a seized device reveals nothing and looks like a fresh install.
///
/// This class is the protocol-level core — deterministic and portable across
/// every AetherNet SDK:
/// <list type="bullet">
/// <item><see cref="DuressPinHash"/> / <see cref="VerifyDuressPin"/> — recognise
///   the duress PIN (SHA-256, constant-time compare); the PIN itself is never
///   stored.</item>
/// <item><see cref="SecureErase"/> — best-effort in-memory erase of key material
///   (overwrite with random, then zero).</item>
/// <item><see cref="IdentityKeyNames"/> + <see cref="PreKeyName"/> /
///   <see cref="SignedPreKeyName"/> — the canonical set of key-store entries a
///   wipe must destroy.</item>
/// </list>
///
/// Destroying the hosting app's local database, platform keychain entries and
/// any decoy store is the app's job — it owns that storage. This class gives the
/// app the crypto trigger, the secure-erase primitive, and the manifest of what
/// to remove, so every app wipes the same identity material the same way.
/// </summary>
public static class PanicWipe
{
    /// <summary>Number of one-time / signed pre-key slots a wipe sweeps (0..N-1).</summary>
    public const int MaxPreKeys = 200;

    /// <summary>
    /// The key-store entry names that together constitute an AetherNet identity —
    /// everything a panic-wipe must destroy, besides the numbered pre-keys.
    /// </summary>
    public static readonly IReadOnlyList<string> IdentityKeyNames = new[]
    {
        "aether_identity_pub",
        "aether_identity_priv",
        "aether_identity_generated",
        "aether_device_salt",
        "aether_drk",
        "aether_ble_rotation_key",
        "aether_ble_irk",
    };

    /// <summary>Key-store name of the i-th one-time pre-key.</summary>
    public static string PreKeyName(int index) => $"prekey_{index}";

    /// <summary>Key-store name of the i-th signed pre-key.</summary>
    public static string SignedPreKeyName(int index) => $"signed_prekey_{index}";

    /// <summary>
    /// The duress-PIN hash: SHA-256 of the UTF-8 PIN. Stored at setup and
    /// compared on unlock — the PIN is only ever kept as this hash.
    /// </summary>
    public static byte[] DuressPinHash(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return SHA256.HashData(Encoding.UTF8.GetBytes(pin));
    }

    /// <summary>
    /// Constant-time check of whether <paramref name="pin"/> matches a stored
    /// <see cref="DuressPinHash"/> — i.e. whether unlocking should trigger a wipe.
    /// </summary>
    public static bool VerifyDuressPin(string pin, byte[] storedHash)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(storedHash);
        if (storedHash.Length != 32) return false;
        return CryptographicOperations.FixedTimeEquals(DuressPinHash(pin), storedHash);
    }

    /// <summary>
    /// Best-effort secure erase of in-memory key material: overwrite with random
    /// bytes, then zero. Call on every buffer holding a secret before releasing
    /// it. Defence in depth — the runtime or OS may still hold copies, but this
    /// removes the obvious one and leaves no plaintext secret in the buffer.
    /// </summary>
    public static void SecureErase(byte[] buffer)
    {
        if (buffer is null || buffer.Length == 0) return;
        RandomNumberGenerator.Fill(buffer);
        CryptographicOperations.ZeroMemory(buffer);
    }
}
