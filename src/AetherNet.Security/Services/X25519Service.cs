// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AetherNet.Security.Services;

/// <summary>
/// X25519 ECDH key agreement primitives — used for the X3DH key exchange in
/// <see cref="SignalProtocolService"/>.
///
/// The .NET BCL's <c>ECDiffieHellman</c> does not support Curve25519 in
/// .NET 10. This service wraps BouncyCastle's X25519 primitives behind a
/// minimal static API so the SignalProtocolService doesn't take a direct
/// dependency on BC types and so the implementation can be swapped (e.g.
/// to libsodium via NSec, or to native BCL once supported) without changes
/// to call sites.
///
/// Public-key wire format: raw 32-byte little-endian Montgomery u-coordinate
/// per RFC 7748 §6.1. No SEC1 prefix, no compressed/uncompressed flag.
/// Same encoding every Signal-Protocol-style implementation uses across
/// the cross-language family.
/// </summary>
internal static class X25519Service
{
    /// <summary>X25519 public key size in bytes.</summary>
    public const int PublicKeySize = 32;
    /// <summary>X25519 private key size in bytes.</summary>
    public const int PrivateKeySize = 32;
    /// <summary>X25519 shared-secret size in bytes (output of one DH op).</summary>
    public const int SharedSecretSize = 32;

    private static readonly SecureRandom Rng = new();

    /// <summary>
    /// Generates a fresh X25519 keypair. Returns raw 32-byte private and
    /// public keys (RFC 7748 encoding).
    /// </summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var generator = new X25519KeyPairGenerator();
        generator.Init(new X25519KeyGenerationParameters(Rng));
        var keyPair = generator.GenerateKeyPair();

        var priv = (X25519PrivateKeyParameters)keyPair.Private;
        var pub = (X25519PublicKeyParameters)keyPair.Public;

        return (priv.GetEncoded(), pub.GetEncoded());
    }

    /// <summary>
    /// Computes the X25519 ECDH shared secret between the local private key
    /// and the remote public key. Returns 32 raw shared-secret bytes
    /// suitable for direct concatenation into an HKDF input.
    /// </summary>
    /// <exception cref="ArgumentException">If either key is not 32 bytes.</exception>
    /// <exception cref="CryptographicException">If the result is the
    /// all-zero point (small-subgroup attack indicator).</exception>
    public static byte[] Agree(byte[] localPrivateKey, byte[] remotePublicKey)
    {
        ArgumentNullException.ThrowIfNull(localPrivateKey);
        ArgumentNullException.ThrowIfNull(remotePublicKey);
        if (localPrivateKey.Length != PrivateKeySize)
            throw new ArgumentException($"X25519 private key must be {PrivateKeySize} bytes (got {localPrivateKey.Length}).", nameof(localPrivateKey));
        if (remotePublicKey.Length != PublicKeySize)
            throw new ArgumentException($"X25519 public key must be {PublicKeySize} bytes (got {remotePublicKey.Length}).", nameof(remotePublicKey));

        var priv = new X25519PrivateKeyParameters(localPrivateKey, 0);
        var pub = new X25519PublicKeyParameters(remotePublicKey, 0);

        var agreement = new X25519Agreement();
        agreement.Init(priv);
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(pub, shared, 0);

        // RFC 7748 §6.1 mandates: implementations MUST check that the
        // result is not the all-zero value (which would indicate a
        // small-subgroup attack via a low-order point).
        if (IsAllZero(shared))
        {
            CryptographicOperations.ZeroMemory(shared);
            throw new CryptographicException(
                "X25519 produced an all-zero shared secret. The remote public key is invalid (low-order point).");
        }

        return shared;
    }

    private static bool IsAllZero(byte[] bytes)
    {
        byte accumulator = 0;
        foreach (var b in bytes) accumulator |= b;
        return accumulator == 0;
    }
}
