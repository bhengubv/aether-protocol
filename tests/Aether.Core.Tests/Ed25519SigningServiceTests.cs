// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using Aether.Security.Services;
using Xunit;

namespace Aether.Core.Tests;

public class Ed25519SigningServiceTests
{
    [Fact]
    public void GenerateSignVerify_RoundTrips()
    {
        var (priv, pub) = Ed25519SigningService.GenerateKeyPair();
        Assert.Equal(32, priv.Length);
        Assert.Equal(32, pub.Length);

        var data = Encoding.UTF8.GetBytes("hello");
        var sig = Ed25519SigningService.Sign(priv, data);
        Assert.Equal(64, sig.Length);

        Assert.True(Ed25519SigningService.Verify(pub, data, sig));
    }

    [Fact]
    public void Verify_TamperedData_ReturnsFalse()
    {
        var (priv, pub) = Ed25519SigningService.GenerateKeyPair();
        var data = Encoding.UTF8.GetBytes("hello");
        var sig = Ed25519SigningService.Sign(priv, data);

        data[0] ^= 0xFF;
        Assert.False(Ed25519SigningService.Verify(pub, data, sig));
    }

    [Fact]
    public void Verify_WrongPublicKey_ReturnsFalse()
    {
        var (priv, _) = Ed25519SigningService.GenerateKeyPair();
        var (_, otherPub) = Ed25519SigningService.GenerateKeyPair();
        var data = Encoding.UTF8.GetBytes("hello");
        var sig = Ed25519SigningService.Sign(priv, data);

        Assert.False(Ed25519SigningService.Verify(otherPub, data, sig));
    }

    [Fact]
    public void VerifyWithFallback_Ed25519Key_WorksLikeVerify()
    {
        var (priv, pub) = Ed25519SigningService.GenerateKeyPair();
        var data = Encoding.UTF8.GetBytes("hello");
        var sig = Ed25519SigningService.Sign(priv, data);

        Assert.True(Ed25519SigningService.VerifyWithFallback(pub, data, sig));
    }

    [Fact]
    public void VerifyWithFallback_LegacyP256Key_RejectedAfterMigrationDeadline()
    {
        // The migration deadline (2026-04-15) has passed — VerifyWithFallback
        // must reject any non-32-byte key without ever invoking ECDsa.
        // (Pre-2026-05-05 the deadline drifted forward forever via
        // UtcNow.AddDays(30), so this rejection never actually fired.)
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p256Pub = ecdsa.ExportSubjectPublicKeyInfo();
        var data = Encoding.UTF8.GetBytes("hello");
        var sig = ecdsa.SignData(data, HashAlgorithmName.SHA256);

        Assert.False(Ed25519SigningService.VerifyWithFallback(p256Pub, data, sig));
    }

    [Fact]
    public void VerifyWithFallback_MalformedKey_ReturnsFalse()
    {
        var data = Encoding.UTF8.GetBytes("hello");
        var sig = new byte[64];
        var malformed = new byte[40]; // not 32, not P-256
        Assert.False(Ed25519SigningService.VerifyWithFallback(malformed, data, sig));
    }

    [Fact]
    public void Sign_WrongPrivateKeyLength_Throws()
    {
        var data = Encoding.UTF8.GetBytes("x");
        Assert.Throws<ArgumentException>(() => Ed25519SigningService.Sign(new byte[31], data));
    }
}
