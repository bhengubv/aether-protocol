// SPDX-License-Identifier: MIT

using NSec.Cryptography;

namespace AetherMesh.Security.Services;

/// <summary>
/// Static Ed25519 signing service using NSec/libsodium.
/// Key format: 32-byte raw seed (private), 32-byte raw point (public), 64-byte signature.
/// </summary>
public sealed class Ed25519SigningService
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// Generates a new Ed25519 key pair.
    /// </summary>
    /// <returns>A tuple of (PrivateKey: 32-byte seed, PublicKey: 32-byte point).</returns>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        using var key = Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        var privateKey = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        return (privateKey, publicKey);
    }

    /// <summary>
    /// Signs data using an Ed25519 private key.
    /// </summary>
    /// <param name="privateKey">32-byte Ed25519 seed.</param>
    /// <param name="data">The data to sign.</param>
    /// <returns>64-byte Ed25519 signature.</returns>
    public static byte[] Sign(byte[] privateKey, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(data);

        if (privateKey.Length != 32)
            throw new ArgumentException("Ed25519 private key must be 32 bytes.", nameof(privateKey));

        using var key = Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        return Algorithm.Sign(key, data);
    }

    /// <summary>
    /// Verifies an Ed25519 signature.
    /// </summary>
    /// <param name="publicKey">32-byte Ed25519 public key.</param>
    /// <param name="data">The signed data.</param>
    /// <param name="signature">64-byte Ed25519 signature.</param>
    /// <returns>True if the signature is valid.</returns>
    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);

        if (publicKey.Length != 32)
            return false;

        if (signature.Length != 64)
            return false;

        var pk = NSec.Cryptography.PublicKey.Import(Algorithm, publicKey, KeyBlobFormat.RawPublicKey);
        return Algorithm.Verify(pk, data, signature);
    }
}
