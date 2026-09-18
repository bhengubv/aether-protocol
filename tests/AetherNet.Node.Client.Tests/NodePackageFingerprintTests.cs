// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Node.Client.Tests;

public class NodePackageFingerprintTests
{
    [Fact]
    public void A_fingerprint_is_43_urlsafe_chars_and_stable()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var fp = NodePackageFingerprint.Compute(bytes);

        Assert.Equal(43, fp.Length);
        Assert.DoesNotContain('+', fp);
        Assert.DoesNotContain('/', fp);
        Assert.DoesNotContain('=', fp);
        Assert.Equal(fp, NodePackageFingerprint.Compute(bytes));
    }

    [Fact]
    public void Verify_accepts_matching_bytes_and_rejects_the_rest()
    {
        var bytes = new byte[] { 9, 8, 7 };
        var fp = NodePackageFingerprint.Compute(bytes);

        Assert.True(NodePackageFingerprint.Verify(bytes, fp));
        Assert.False(NodePackageFingerprint.Verify(new byte[] { 9, 8, 6 }, fp));
        Assert.False(NodePackageFingerprint.Verify(bytes, "not-a-real-fingerprint"));
    }
}
