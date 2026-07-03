// SPDX-License-Identifier: MIT

using AetherNet.Security.Models;
using AetherNet.Security.Services;

namespace AetherNet.Security.Backup;

/// <summary>
/// Recovery-phrase backup and restore for an AetherNet identity.
///
/// An AetherNet identity is an Ed25519 key pair whose private key is a 32-byte
/// seed — exactly 256 bits, which map cleanly onto a 24-word BIP-39 phrase. The
/// user writes down 24 ordinary words; from those words alone the identity is
/// fully reconstructed on any device. No server, no account, no custodian holds
/// anything — the phrase <em>is</em> the identity.
/// </summary>
public static class IdentityBackup
{
    /// <summary>
    /// Produces the 24-word recovery phrase for an identity's private key.
    /// </summary>
    /// <param name="ed25519PrivateKey">The 32-byte Ed25519 private seed
    /// (as returned by <see cref="Ed25519SigningService.GenerateKeyPair"/>).</param>
    public static string ToRecoveryPhrase(byte[] ed25519PrivateKey)
    {
        ArgumentNullException.ThrowIfNull(ed25519PrivateKey);
        if (ed25519PrivateKey.Length != 32)
            throw new ArgumentException(
                "An AetherNet identity private key must be 32 bytes.", nameof(ed25519PrivateKey));

        return Bip39Mnemonic.EntropyToMnemonic(ed25519PrivateKey);
    }

    /// <summary>
    /// Restores a full identity key pair from a 24-word recovery phrase. The
    /// BIP-39 checksum is enforced, so a mistyped word is rejected rather than
    /// silently reconstructing a different identity.
    /// </summary>
    /// <exception cref="FormatException">The phrase is malformed, fails its
    /// checksum, or does not encode a 256-bit (24-word) identity seed.</exception>
    public static KeyPair FromRecoveryPhrase(string recoveryPhrase)
    {
        ArgumentNullException.ThrowIfNull(recoveryPhrase);

        var privateKey = Bip39Mnemonic.MnemonicToEntropy(recoveryPhrase);
        if (privateKey.Length != 32)
            throw new FormatException(
                "An AetherNet recovery phrase must be 24 words (a 256-bit identity seed).");

        var publicKey = Ed25519SigningService.DerivePublicKey(privateKey);
        return new KeyPair(publicKey, privateKey);
    }
}
