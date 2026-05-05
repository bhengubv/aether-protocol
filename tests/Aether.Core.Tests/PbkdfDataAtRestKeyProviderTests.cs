// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using Aether.Storage;
using Xunit;

namespace Aether.Core.Tests;

/// <summary>
/// Tests for <see cref="DerivedDataAtRestKeyProvider"/>: passphrase-derived AES-256
/// keys via PBKDF2-HMAC-SHA256. The production iteration count is OWASP's 2023
/// recommendation (600,000); these tests use a much smaller count to keep the
/// suite fast while still verifying correctness, determinism, and salt
/// domain-separation.
/// </summary>
public class PbkdfDataAtRestKeyProviderTests
{
    /// <summary>
    /// Iteration count used in tests. 1,000 finishes in milliseconds — production
    /// uses <see cref="DerivedDataAtRestKeyProvider.DefaultIterations"/> (600,000).
    /// Never pass this small number into a real deployment.
    /// </summary>
    private const int TestIterations = 1_000;

    private static byte[] FixedSalt(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        // 32-byte salt — well above the 16-byte minimum.
        return hash;
    }

    [Fact]
    public void SamePassphraseAndSalt_DeriveIdenticalKey()
    {
        var salt = FixedSalt("device-A");
        var p1 = new DerivedDataAtRestKeyProvider("hunter2", salt, TestIterations);
        var p2 = new DerivedDataAtRestKeyProvider("hunter2", salt, TestIterations);

        var k1 = p1.GetKey(p1.CurrentVersion);
        var k2 = p2.GetKey(p2.CurrentVersion);

        Assert.NotNull(k1);
        Assert.NotNull(k2);
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void DifferentSalt_ProducesDifferentKey()
    {
        var saltA = FixedSalt("device-A");
        var saltB = FixedSalt("device-B");

        var pA = new DerivedDataAtRestKeyProvider("hunter2", saltA, TestIterations);
        var pB = new DerivedDataAtRestKeyProvider("hunter2", saltB, TestIterations);

        var kA = pA.GetKey(pA.CurrentVersion);
        var kB = pB.GetKey(pB.CurrentVersion);

        Assert.NotNull(kA);
        Assert.NotNull(kB);
        Assert.NotEqual(kA, kB);
    }

    [Fact]
    public void DifferentPassphrase_ProducesDifferentKey()
    {
        var salt = FixedSalt("device-A");
        var p1 = new DerivedDataAtRestKeyProvider("hunter2", salt, TestIterations);
        var p2 = new DerivedDataAtRestKeyProvider("hunter3", salt, TestIterations);

        Assert.NotEqual(p1.GetKey(1), p2.GetKey(1));
    }

    [Fact]
    public void DerivedKeyLength_IsExactly32Bytes()
    {
        var p = new DerivedDataAtRestKeyProvider("hunter2", FixedSalt("d"), TestIterations);
        var k = p.GetKey(p.CurrentVersion);

        Assert.NotNull(k);
        Assert.Equal(32, k!.Length);
    }

    [Fact]
    public void CurrentVersion_DefaultsToOne()
    {
        var p = new DerivedDataAtRestKeyProvider("pw", FixedSalt("d"), TestIterations);
        Assert.Equal(1, p.CurrentVersion);
        Assert.NotNull(p.GetKey(1));
        Assert.Null(p.GetKey(2));
    }

    [Fact]
    public void EmptyPassphrase_IsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new DerivedDataAtRestKeyProvider(string.Empty, FixedSalt("d"), TestIterations));
    }

    [Fact]
    public void NullPassphrase_IsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new DerivedDataAtRestKeyProvider(null!, FixedSalt("d"), TestIterations));
    }

    [Fact]
    public void NullSalt_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DerivedDataAtRestKeyProvider("pw", null!, TestIterations));
    }

    [Fact]
    public void TooShortSalt_IsRejected()
    {
        var saltTooShort = new byte[15];
        Assert.Throws<ArgumentException>(() =>
            new DerivedDataAtRestKeyProvider("pw", saltTooShort, TestIterations));
    }

    [Fact]
    public void NonPositiveIterations_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DerivedDataAtRestKeyProvider("pw", FixedSalt("d"), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DerivedDataAtRestKeyProvider("pw", FixedSalt("d"), -1));
    }

    [Fact]
    public void ProductionDefaultIterations_IsOwasp2023Recommendation()
    {
        // Documents the canonical default — 600,000 per OWASP Password Storage
        // Cheat Sheet (2023) for PBKDF2-HMAC-SHA256.
        Assert.Equal(600_000, DerivedDataAtRestKeyProvider.DefaultIterations);
    }

    [Fact]
    public void Iterations_AreEnforced_DifferentCountsProduceDifferentKeys()
    {
        var salt = FixedSalt("d");
        var p1 = new DerivedDataAtRestKeyProvider("pw", salt, 1_000);
        var p2 = new DerivedDataAtRestKeyProvider("pw", salt, 2_000);

        var k1 = p1.GetKey(p1.CurrentVersion);
        var k2 = p2.GetKey(p2.CurrentVersion);

        Assert.NotEqual(k1, k2);
        Assert.Equal(1_000, p1.Iterations);
        Assert.Equal(2_000, p2.Iterations);
    }

    [Fact]
    public void WithRotation_AddsNewVersion_KeepsPreviousForDecryption()
    {
        var saltOld = FixedSalt("device-A");
        var saltNew = FixedSalt("device-A-rotated");

        var initial = new DerivedDataAtRestKeyProvider("hunter2", saltOld, TestIterations);
        var rotated = initial.WithRotation(2, "hunter3", saltNew, TestIterations);

        // New version is current.
        Assert.Equal(2, rotated.CurrentVersion);

        // Both versions have keys.
        var kOld = rotated.GetKey(1);
        var kNew = rotated.GetKey(2);
        Assert.NotNull(kOld);
        Assert.NotNull(kNew);
        Assert.NotEqual(kOld, kNew);

        // Old version still matches the original derivation.
        Assert.Equal(initial.GetKey(1), kOld);
    }

    [Fact]
    public void WithRotation_RejectsDuplicateVersion()
    {
        var p = new DerivedDataAtRestKeyProvider("pw", FixedSalt("d"), TestIterations);
        Assert.Throws<ArgumentException>(() =>
            p.WithRotation(1, "pw2", FixedSalt("d2"), TestIterations));
    }

    [Fact]
    public void WithRotation_RejectsOutOfRangeVersion()
    {
        var p = new DerivedDataAtRestKeyProvider("pw", FixedSalt("d"), TestIterations);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            p.WithRotation(0, "pw2", FixedSalt("d2"), TestIterations));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            p.WithRotation(256, "pw2", FixedSalt("d2"), TestIterations));
    }

    [Fact]
    public void StaticProvider_RejectsWrongLengthKey()
    {
        var tooShort = new byte[16];
        Assert.Throws<ArgumentException>(() => new StaticDataAtRestKeyProvider(tooShort));

        var tooLong = new byte[64];
        Assert.Throws<ArgumentException>(() => new StaticDataAtRestKeyProvider(tooLong));
    }

    [Fact]
    public void StaticProvider_DefensiveCopy_CallerMutationsDontLeak()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)i;

        var provider = new StaticDataAtRestKeyProvider(k);
        var snapshot = provider.GetKey(1)!;
        var snapshotCopy = (byte[])snapshot.Clone();

        // Mutate the original input — must not affect the stored key.
        k[0] = 0xFF;
        Assert.Equal(snapshotCopy, provider.GetKey(1));
    }

    [Fact]
    public void StaticProvider_RejectsCurrentVersionNotInDictionary()
    {
        var keys = new Dictionary<int, byte[]> { [1] = new byte[32] };
        Assert.Throws<ArgumentException>(() =>
            new StaticDataAtRestKeyProvider(keys, currentVersion: 2));
    }

    [Fact]
    public void StaticProvider_RejectsOutOfRangeVersions()
    {
        var keys = new Dictionary<int, byte[]> { [1] = new byte[32] };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StaticDataAtRestKeyProvider(keys, currentVersion: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StaticDataAtRestKeyProvider(keys, currentVersion: 256));
    }
}
