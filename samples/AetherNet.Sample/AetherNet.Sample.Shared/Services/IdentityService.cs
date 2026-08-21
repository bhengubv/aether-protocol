// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Who this device is, as the app sees it.
///
/// <para>
/// The app does not create this and does not own it. The identity belongs to the <b>device</b> — it is
/// the device's address on the mesh, the way an IP address belongs to the machine rather than to any
/// program running on it. This app asks the node for it, and the node mints one only if the device has
/// never had one.
/// </para>
///
/// <para>
/// That is why there is no private key on this interface. An app that holds the device's key is an app
/// claiming ownership of the device's address, and if every app does that then a phone with fifteen
/// apps is fifteen nodes on the mesh — fifteen presence beacons, fifteen entries in every neighbour's
/// routing table, fifteen reputations for one person. When something must be signed, this asks the node
/// and gets a signature back.
/// </para>
/// </summary>
public interface IIdentityService
{
    /// <summary>The shareable AetherTag (e.g. <c>KXJB7-MN2P4</c>) — this device's address.</summary>
    string AetherTag { get; }

    /// <summary>The public key the tag is derived from.</summary>
    byte[] PublicKey { get; }

    /// <summary>
    /// The secret behind this device's rotating wire address. Derived from the identity and useful for
    /// nothing else — the identity itself never leaves the node.
    /// </summary>
    byte[] RoutingKey { get; }

    /// <summary>True when this run is the first time this device has ever had an identity.</summary>
    bool IsNewIdentity { get; }

    /// <summary>How the identity is protected on this device, for the UI to state honestly.</summary>
    string ProtectionDescription { get; }

    /// <summary>Sign bytes as this device.</summary>
    byte[] Sign(byte[] data);
}

/// <inheritdoc />
public sealed class IdentityService : IIdentityService
{
    /// <summary>What the wire address is derived for. Named for the use, not for this app.</summary>
    private const string RoutingPurpose = "erid-routing";

    private readonly INodeIdentity _node;

    public IdentityService(INodeIdentity node, ISecretVault vault, AetherStore store)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(store);

        _node = node;
        ProtectionDescription = vault.ProtectionDescription;

        // Whether the device had an identity before this run — asked before anything mints one.
        IsNewIdentity = !new VaultNodeIdentityStore(vault).Exists;

        // Deliberately NOT resolved here.
        //
        // These are three round trips to the hardware keystore, and a constructor runs wherever it is
        // resolved from. In Blazor Hybrid the .NET dispatcher, the WebView thread and the Android main
        // thread are one thread, so a page that injects this froze the interface until the keystore
        // answered — and the app worked around it by racing a background warm-up rather than fixing
        // it. Behind a Lazy, resolving costs nothing and the warm-up below does the waiting.
        _identity = new Lazy<Resolved>(() =>
        {
            var tag = _node.GetOrMintAsync().AsTask().GetAwaiter().GetResult().Value;
            var publicKey = _node.GetPublicKeyAsync().AsTask().GetAwaiter().GetResult();
            var routingKey = _node.DeriveKeyAsync(RoutingPurpose).AsTask().GetAwaiter().GetResult();

            // Keep the local mirror honest if it drifted (fresh database, restored backup, older build).
            var mirrored = store.GetIdentity();
            if (mirrored is null || mirrored.Value.Tag != tag) store.SaveIdentity(tag, publicKey);

            return new Resolved(tag, publicKey, routingKey);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private readonly Lazy<Resolved> _identity;

    private readonly record struct Resolved(string Tag, byte[] PublicKey, byte[] RoutingKey);

    /// <summary>
    /// Unseal the identity off the UI thread, before anything asks for it.
    /// </summary>
    /// <remarks>
    /// Called by the warm-up. Reading any of the properties without this still works — it simply
    /// blocks, exactly as the constructor used to — so nothing breaks if a path is missed.
    /// </remarks>
    public Task PrepareAsync() => Task.Run(() => _ = _identity.Value);

    public string AetherTag => _identity.Value.Tag;
    public byte[] PublicKey => _identity.Value.PublicKey;
    public byte[] RoutingKey => _identity.Value.RoutingKey;
    public bool IsNewIdentity { get; }
    public string ProtectionDescription { get; }

    /// <inheritdoc />
    public byte[] Sign(byte[] data) => _node.SignAsync(data).AsTask().GetAwaiter().GetResult();
}
