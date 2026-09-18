// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace AetherNet.Node.Client;

/// <summary>
/// The integrity check every install path runs before a byte is installed: the SHA-256 of the APK, encoded
/// url-safe base64 without padding — the same shape the platform's managed-provisioning record uses, so a
/// fingerprint advertised over NFC, a URL, or the mesh verifies identically everywhere.
/// </summary>
public static class NodePackageFingerprint
{
    /// <summary>The url-safe, unpadded base64 SHA-256 of <paramref name="packageBytes"/> (43 characters).</summary>
    public static string Compute(ReadOnlySpan<byte> packageBytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(packageBytes, hash);
        return System.Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>True when <paramref name="packageBytes"/> hashes to <paramref name="expectedFingerprint"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> packageBytes, string expectedFingerprint)
        => string.Equals(Compute(packageBytes), expectedFingerprint, System.StringComparison.Ordinal);
}
