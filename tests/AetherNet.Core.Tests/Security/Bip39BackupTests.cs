// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Security.Backup;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Recovery-phrase backup/restore for an AetherNet identity: a freshly generated
/// Ed25519 key pair round-trips through a 24-word phrase, the restored key still
/// signs and verifies, and tampered / malformed phrases are rejected (never a
/// silent wrong identity). Fully offline — no server involved.
/// </summary>
public class Bip39BackupTests
{
    [Fact]
    public void GeneratedIdentity_RoundTrips_ThroughRecoveryPhrase()
    {
        var (privateKey, publicKey) = Ed25519SigningService.GenerateKeyPair();

        var phrase = IdentityBackup.ToRecoveryPhrase(privateKey);
        Assert.Equal(24, phrase.Split(' ').Length);

        var restored = IdentityBackup.FromRecoveryPhrase(phrase);
        Assert.Equal(privateKey, restored.PrivateKey);
        Assert.Equal(publicKey, restored.PublicKey);
    }

    [Fact]
    public void RestoredIdentity_CanStillSign_AndVerifies()
    {
        var (privateKey, _) = Ed25519SigningService.GenerateKeyPair();
        var restored = IdentityBackup.FromRecoveryPhrase(IdentityBackup.ToRecoveryPhrase(privateKey));

        var message = Encoding.UTF8.GetBytes("aethernet identity backup");
        var signature = Ed25519SigningService.Sign(restored.PrivateKey, message);
        Assert.True(Ed25519SigningService.Verify(restored.PublicKey, message, signature));
    }

    [Fact]
    public void KnownEntropy_ProducesKnownPhrase_AndRestores()
    {
        // Official Trezor 256-bit vector: entropy -> canonical 24-word phrase.
        var entropy = Convert.FromHexString(
            "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f");

        var phrase = IdentityBackup.ToRecoveryPhrase(entropy);
        Assert.Equal(
            "void come effort suffer camp survey warrior heavy shoot primary clutch crush " +
            "open amazing screen patrol group space point ten exist slush involve unfold",
            phrase);

        Assert.Equal(entropy, IdentityBackup.FromRecoveryPhrase(phrase).PrivateKey);
    }

    [Fact]
    public void InvalidChecksum_IsRejected()
    {
        // 24x "abandon" is all-zero bits: the checksum cannot match SHA-256(0^32).
        var phrase = string.Join(' ', Enumerable.Repeat("abandon", 24));
        Assert.False(Bip39Mnemonic.IsValid(phrase));
        Assert.Throws<FormatException>(() => IdentityBackup.FromRecoveryPhrase(phrase));
    }

    [Fact]
    public void UnknownWord_IsRejected()
    {
        var (privateKey, _) = Ed25519SigningService.GenerateKeyPair();
        var words = IdentityBackup.ToRecoveryPhrase(privateKey).Split(' ');
        words[5] = "notabip39word";
        Assert.Throws<FormatException>(() => IdentityBackup.FromRecoveryPhrase(string.Join(' ', words)));
    }

    [Fact]
    public void WrongWordCount_IsRejected()
    {
        Assert.Throws<FormatException>(() => IdentityBackup.FromRecoveryPhrase("abandon abandon abandon"));
    }

    [Fact]
    public void NonIdentitySizedPrivateKey_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => IdentityBackup.ToRecoveryPhrase(new byte[16]));
    }
}
