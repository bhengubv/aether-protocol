// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <inheritdoc cref="AetherNet.Mesh.IIdentityService" />
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

            // Keep the local mirror honest if it drifted (fresh database, restored backup, older build).
            var mirrored = store.GetIdentity();
            if (mirrored is null || mirrored.Value.Tag != tag) store.SaveIdentity(tag, publicKey);

            return new Resolved(tag, publicKey);
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        // Derived on first use, not with the identity. An app connected to AetherNetService never holds this
        // key — a derived key never leaves the node — so the tag must resolve without it. Only a host that runs
        // the radios itself ever asks for it.
        _routingKey = new Lazy<byte[]>(
            () => _node.DeriveKeyAsync(RoutingPurpose).AsTask().GetAwaiter().GetResult(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private readonly Lazy<Resolved> _identity;
    private readonly Lazy<byte[]> _routingKey;

    private readonly record struct Resolved(string Tag, byte[] PublicKey);

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
    public byte[] RoutingKey => _routingKey.Value;
    public bool IsNewIdentity { get; }
    public string ProtectionDescription { get; }

    /// <inheritdoc />
    public byte[] Sign(byte[] data) => _node.SignAsync(data).AsTask().GetAwaiter().GetResult();
}
