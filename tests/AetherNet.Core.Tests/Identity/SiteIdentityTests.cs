// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AetherNet.Identity;
using Xunit;

namespace AetherNet.Core.Tests.Identity;

public class SiteIdentityTests
{
    private const string SiteA = "BH8CZ-B09CA";
    private const string SiteB = "DY5CF-84G9T";

    // A minimal INodeIdentity whose per-purpose key is HKDF-SHA256 of a fixed root — enough to exercise
    // the per-site derivation deterministically without a platform keystore.
    private sealed class FakeIdentity(byte[] root) : INodeIdentity
    {
        public ValueTask<AetherNetTag> GetOrMintAsync(CancellationToken ct = default)
            => new(AetherNetTag.FromPublicKey(root));
        public ValueTask<byte[]> GetPublicKeyAsync(CancellationToken ct = default) => new(root);
        public ValueTask<byte[]> SignAsync(byte[] data, CancellationToken ct = default)
            => new(SHA256.HashData(data));
        public ValueTask<byte[]> DeriveKeyAsync(string purpose, CancellationToken ct = default)
            => new(HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, salt: null,
                info: Encoding.UTF8.GetBytes(purpose)));
    }

    private static FakeIdentity Device(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    [Fact]
    public async Task ForSite_IsDeterministic_ForOneDeviceAndSite()
    {
        var device = Device(1);
        var a = await SiteIdentityDerivation.ForSiteAsync(device, SiteA);
        var b = await SiteIdentityDerivation.ForSiteAsync(device, SiteA);
        Assert.Equal(a.Pseudonym, b.Pseudonym);
        Assert.Equal(a.SiteSecret, b.SiteSecret);
        Assert.Equal(32, a.SiteSecret.Length);
    }

    [Fact]
    public async Task ForSite_IsUnlinkableAcrossSites()
    {
        var device = Device(1);
        var atA = await SiteIdentityDerivation.ForSiteAsync(device, SiteA);
        var atB = await SiteIdentityDerivation.ForSiteAsync(device, SiteB);
        Assert.NotEqual(atA.Pseudonym, atB.Pseudonym);
        Assert.NotEqual(atA.SiteSecret, atB.SiteSecret);
    }

    [Fact]
    public async Task ForSite_DiffersAcrossDevices_ForTheSameSite()
    {
        var one = await SiteIdentityDerivation.ForSiteAsync(Device(1), SiteA);
        var two = await SiteIdentityDerivation.ForSiteAsync(Device(2), SiteA);
        Assert.NotEqual(one.Pseudonym, two.Pseudonym);
    }

    [Fact]
    public async Task ForSite_CanonicalisesTheSiteTag_SoADashlessInputIsTheSameSite()
    {
        var device = Device(1);
        var dashed = await SiteIdentityDerivation.ForSiteAsync(device, SiteA);
        var dashless = await SiteIdentityDerivation.ForSiteAsync(device, SiteA.Replace("-", ""));
        Assert.Equal(dashed.Pseudonym, dashless.Pseudonym);
    }

    [Fact]
    public async Task Pseudonym_IsTagShaped()
    {
        var id = await SiteIdentityDerivation.ForSiteAsync(Device(1), SiteA);
        Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$"), id.Pseudonym);
    }

    [Fact]
    public async Task ForSite_RejectsAnInvalidSiteTag()
        => await Assert.ThrowsAsync<ArgumentException>(
            async () => await SiteIdentityDerivation.ForSiteAsync(Device(1), "not-a-tag"));

    [Fact]
    public void PseudonymFor_IsDeterministic_AndRejectsEmpty()
    {
        var secret = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Equal(SiteIdentityDerivation.PseudonymFor(secret), SiteIdentityDerivation.PseudonymFor(secret));
        Assert.Throws<ArgumentException>(() => SiteIdentityDerivation.PseudonymFor([]));
    }
}
