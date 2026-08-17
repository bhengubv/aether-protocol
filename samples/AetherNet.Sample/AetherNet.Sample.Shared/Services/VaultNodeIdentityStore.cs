// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Keeps this device's node identity in the platform vault.
///
/// <para>
/// The store is the part of the arrangement that is genuinely platform-specific: it decides <i>where</i>
/// the device's identity lives and <i>what</i> has to be satisfied to open it — the device's own
/// authentication, ideally hardware-backed.
/// </para>
///
/// <para>
/// <b>Honest limit on stock Android and iOS.</b> Both give an application a vault that is private to
/// that application and unreadable by any other, with no neutral location an unrelated app could share.
/// So on those platforms this store is per-application, and two apps on one handset are still two nodes.
/// That is a property of the platform, not of the design: point this at a store the device owns rather
/// than one an app owns — a system service, or an OS that provides one — and the same code above it
/// becomes one node per device with nothing rewritten.
/// </para>
/// </summary>
public sealed class VaultNodeIdentityStore : INodeIdentityStore
{
    /// <summary>The one name the device's identity is filed under. Never per-app, never per-install.</summary>
    private const string KeyName = "aether.node.identity";

    private readonly ISecretVault _vault;

    public VaultNodeIdentityStore(ISecretVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
    }

    /// <inheritdoc />
    public bool Exists => _vault.Has(KeyName) || _vault.Has(LegacyKeyName);

    /// <inheritdoc />
    public byte[]? Load()
    {
        try
        {
            // Adopt an identity minted by an earlier build before it had a node to ask. Changing the
            // name a device files its identity under must never cost the device its identity.
            return _vault.Get(KeyName) ?? _vault.Get(LegacyKeyName);
        }
        catch (SecretUnavailableException ex)
        {
            // "Not now" has to stay "not now" all the way up. Flattened to "nothing here", the node
            // mints a replacement and this device's address changes for good.
            throw new NodeIdentityUnavailableException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public void Save(byte[] privateKey) => _vault.Set(KeyName, privateKey);

    /// <summary>What earlier builds of this sample called it, when the app still minted its own.</summary>
    private const string LegacyKeyName = "aether.identity.ed25519";
}
