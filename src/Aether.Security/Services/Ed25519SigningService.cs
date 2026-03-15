// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

namespace Aether.Security.Services;

/// <summary>
/// Static Ed25519 signing service using NSec/libsodium.
/// Key format: 32-byte raw seed (private), 32-byte raw point (public), 64-byte signature.
/// </summary>
public sealed class Ed25519SigningService
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// How many P-256 legacy verifications have been performed.
    /// Used to track migration progress away from legacy curve.
    /// </summary>
    public static long P256VerificationCount => Interlocked.Read(ref _p256Count);

    /// <summary>
    /// The cutoff date after which P-256 legacy signatures are no longer accepted.
    /// Set to 30 days from first assembly load.
    /// </summary>
    private static readonly DateTimeOffset P256MigrationDeadline = DateTimeOffset.UtcNow.AddDays(30);

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

    /// <summary>
    /// Verifies a signature with fallback support for legacy P-256 keys during migration.
    /// If the public key is 32 bytes, uses Ed25519. If longer, attempts P-256 ECDSA
    /// verification but only within the 30-day migration window.
    /// </summary>
    /// <param name="publicKey">Public key bytes (32 = Ed25519, 65 = P-256 uncompressed).</param>
    /// <param name="data">The signed data.</param>
    /// <param name="signature">The signature bytes.</param>
    /// <returns>True if the signature is valid.</returns>
    public static bool VerifyWithFallback(byte[] publicKey, byte[] data, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);

        // Standard Ed25519 path
        if (publicKey.Length == 32)
        {
            return Verify(publicKey, data, signature);
        }

        // Legacy P-256 path — only valid during migration window
        if (DateTimeOffset.UtcNow > P256MigrationDeadline)
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);

            var result = ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
            if (result)
            {
                Interlocked.Increment(ref _p256Count);
            }
            return result;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static long _p256Count;
}
