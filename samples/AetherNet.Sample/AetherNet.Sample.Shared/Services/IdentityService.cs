// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Data;
using AetherNet.Security.Services;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// This device's own AetherNet identity — one Ed25519 keypair, one shareable AetherTag, <b>for the
/// life of the install</b>. No account, no phone number, no server: the tag <i>is</i> the identity,
/// derived from the public key and verifiable by anyone.
///
/// It has to be permanent. Someone who adds you today must still reach you tomorrow, and a card you
/// published must still verify against the tag you're showing — so the keypair is generated once and
/// sealed in the platform vault (hardware-backed on a phone), never regenerated.
/// </summary>
public interface IIdentityService
{
    /// <summary>The shareable AetherTag (e.g. <c>KXJB7-MN2P4</c>) — this is who you are.</summary>
    string AetherTag { get; }

    /// <summary>The public key the tag is derived from.</summary>
    byte[] PublicKey { get; }

    /// <summary>The private key. Never leaves the device.</summary>
    byte[] PrivateKey { get; }

    /// <summary>True when this run generated a brand-new identity (a genuine first run).</summary>
    bool IsNewIdentity { get; }

    /// <summary>How the private key is protected on this device, for the UI to state honestly.</summary>
    string ProtectionDescription { get; }
}

/// <inheritdoc />
public sealed class IdentityService : IIdentityService
{
    private const string VaultKeyName = "aether.identity.ed25519";

    public IdentityService(ISecretVault vault, AetherStore store)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(store);

        ProtectionDescription = vault.ProtectionDescription;

        var sealedKey = vault.Get(VaultKeyName);
        if (sealedKey is { Length: > 0 })
        {
            // Returning user: rebuild everything from the one stored secret. The public key is derived
            // rather than trusted from the database, so a tampered DB row cannot swap your identity.
            PrivateKey = sealedKey;
            PublicKey = Ed25519SigningService.DerivePublicKey(sealedKey);
            AetherTag = AetherNetTag.FromPublicKey(PublicKey).Value;
            IsNewIdentity = false;

            // Keep the mirror honest if it drifted (fresh DB, restored backup, older build).
            var mirrored = store.GetIdentity();
            if (mirrored is null || mirrored.Value.Tag != AetherTag)
                store.SaveIdentity(AetherTag, PublicKey);
            return;
        }

        // First run: mint once, seal, and mirror the public half for display without unsealing.
        var (privateKey, publicKey) = Ed25519SigningService.GenerateKeyPair();
        PrivateKey = privateKey;
        PublicKey = publicKey;
        AetherTag = AetherNetTag.FromPublicKey(publicKey).Value;
        IsNewIdentity = true;

        vault.Set(VaultKeyName, privateKey);
        store.SaveIdentity(AetherTag, publicKey);
    }

    public string AetherTag { get; }
    public byte[] PublicKey { get; }
    public byte[] PrivateKey { get; }
    public bool IsNewIdentity { get; }
    public string ProtectionDescription { get; }
}
