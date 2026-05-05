// SPDX-License-Identifier: MIT

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
    public void Sign_WrongPrivateKeyLength_Throws()
    {
        var data = Encoding.UTF8.GetBytes("x");
        Assert.Throws<ArgumentException>(() => Ed25519SigningService.Sign(new byte[31], data));
    }
}
