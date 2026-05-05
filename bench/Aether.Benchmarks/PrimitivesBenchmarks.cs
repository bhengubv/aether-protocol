// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Security.Cryptography;
using Aether.Security.Services;
using BenchmarkDotNet.Attributes;

namespace Aether.Benchmarks;

/// <summary>
/// Bottom-of-the-stack crypto primitives. The Double Ratchet's inner loop
/// is dominated by these operations:
///
///   * X25519 ECDH agreement   — 2x per DH-ratchet step
///   * HMAC-SHA256             — 2x per chain step (message key + next chain)
///   * HKDF-SHA256             — 1x per DH-ratchet step (KDF_RK, 64 bytes out)
///   * Ed25519 sign/verify     — 1x per packet on the mesh
///
/// Pinning baselines for these makes BCL/NSec/BouncyCastle version bumps
/// visible without spelunking through Signal-protocol-level results.
///
/// Note: <c>X25519Service</c> is <c>internal</c> to <c>Aether.Security</c>.
/// Rather than expand <c>InternalsVisibleTo</c> from production code, we
/// reflect into it the same way the existing fixture tests do
/// (<c>SignalFixtureTests.cs</c>).
/// </summary>
[MemoryDiagnoser]
public class PrimitivesBenchmarks
{
    // X25519 — accessed via reflection (X25519Service is internal).
    private static readonly MethodInfo X25519GenerateKeyPair = ResolveX25519Method("GenerateKeyPair");
    private static readonly MethodInfo X25519Agree = ResolveX25519Method("Agree");

    private byte[] _x25519PrivA = null!;
    private byte[] _x25519PubB = null!;

    // HMAC + HKDF — single chain-key bytes plus the standard 0x01 input.
    private byte[] _chainKey = null!;
    private static readonly byte[] HmacInput = [0x01];
    private byte[] _kdfIkm = null!;
    private byte[] _kdfSalt = null!;
    private static readonly byte[] KdfInfo = "aether-ratchet-rk-v1"u8.ToArray();

    // Ed25519 — uses the project's existing static signing service.
    private byte[] _edPriv = null!;
    private byte[] _edPub = null!;
    private byte[] _edData = null!;
    private byte[] _edSignature = null!;

    [GlobalSetup]
    public void Setup()
    {
        // X25519 — generate two keypairs, take A's priv and B's pub.
        var kpA = ((byte[] Priv, byte[] Pub))X25519GenerateKeyPair.Invoke(null, null)!;
        var kpB = ((byte[] Priv, byte[] Pub))X25519GenerateKeyPair.Invoke(null, null)!;
        _x25519PrivA = kpA.Priv;
        _x25519PubB = kpB.Pub;

        // HMAC chain key — 32 random bytes, same shape as a Signal chain key.
        _chainKey = RandomNumberGenerator.GetBytes(32);

        // HKDF — typical KDF_RK invocation: 32-byte DH output as IKM,
        // 32-byte root key as salt, fixed info string.
        _kdfIkm = RandomNumberGenerator.GetBytes(32);
        _kdfSalt = RandomNumberGenerator.GetBytes(32);

        // Ed25519 — generate keypair, sign a 64-byte message body
        // (representative of the canonical signable-data layout for a
        // small Data packet).
        (_edPriv, _edPub) = Ed25519SigningService.GenerateKeyPair();
        _edData = RandomNumberGenerator.GetBytes(64);
        _edSignature = Ed25519SigningService.Sign(_edPriv, _edData);
    }

    [Benchmark]
    public byte[] X25519Service_Agree()
    {
        // One DH op — what every X3DH and DH-ratchet step calls 4x and 2x respectively.
        return (byte[])X25519Agree.Invoke(null, [_x25519PrivA, _x25519PubB])!;
    }

    [Benchmark]
    public byte[] Hmac_Sha256_OneByte()
    {
        // The ratchet inner loop: HMAC(chainKey, 0x01) -> message key.
        // Tracking this in isolation surfaces BCL HMAC perf changes.
        return HMACSHA256.HashData(_chainKey, HmacInput);
    }

    [Benchmark]
    public byte[] Hkdf_Sha256_64Bytes()
    {
        // KDF_RK per Signal §5.2 — 32-byte new root + 32-byte new chain.
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: _kdfIkm,
            outputLength: 64,
            salt: _kdfSalt,
            info: KdfInfo);
    }

    [Benchmark]
    public byte[] Ed25519_Sign() => Ed25519SigningService.Sign(_edPriv, _edData);

    [Benchmark]
    public bool Ed25519_Verify() => Ed25519SigningService.Verify(_edPub, _edData, _edSignature);

    private static MethodInfo ResolveX25519Method(string name)
    {
        var type = typeof(Ed25519SigningService).Assembly
            .GetType("Aether.Security.Services.X25519Service")
            ?? throw new InvalidOperationException(
                "X25519Service type not found in Aether.Security assembly. " +
                "Has the type been moved or made public? Update PrimitivesBenchmarks.");
        return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"X25519Service.{name} not found.");
    }
}
