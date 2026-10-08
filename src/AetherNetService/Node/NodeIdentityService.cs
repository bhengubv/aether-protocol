// SPDX-License-Identifier: MIT
using AetherNet.Identity;
using AetherNet.Mesh;

namespace AetherNetService;

/// <summary>
/// The node's own <see cref="IIdentityService"/>, over the identity this device holds locally
/// (<see cref="INodeIdentity"/>). The mesh — <c>RadioMeshSender</c>, the Circle directory — asks this for
/// the device's AetherTag and its routing key exactly as a consumer app does; the difference is only that
/// here the node <b>is</b> the identity, so there is no bind in the middle. The private key still never
/// leaves the node: signing goes through <see cref="INodeIdentity.SignAsync"/>.
/// </summary>
internal sealed class NodeIdentityService : IIdentityService
{
    /// <summary>What the wire address is derived for. Named for the use, not for this app.</summary>
    private const string RoutingPurpose = "erid-routing";

    private readonly INodeIdentity _node;
    private readonly Lazy<Resolved> _identity;

    public NodeIdentityService(INodeIdentity node)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));

        // Behind a Lazy: resolving is a few round trips to the key store, and whoever touches a property
        // first pays for them. Unsealed once, off the main thread, before the first send needs it.
        _identity = new Lazy<Resolved>(() =>
        {
            var tag = _node.GetOrMintAsync().AsTask().GetAwaiter().GetResult().Value;
            var publicKey = _node.GetPublicKeyAsync().AsTask().GetAwaiter().GetResult();
            var routingKey = _node.DeriveKeyAsync(RoutingPurpose).AsTask().GetAwaiter().GetResult();
            return new Resolved(tag, publicKey, routingKey);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Unseal the identity off the calling thread, before anything asks for it.</summary>
    public Task PrepareAsync() => Task.Run(() => _ = _identity.Value);

    public string AetherTag => _identity.Value.Tag;
    public byte[] PublicKey => _identity.Value.PublicKey;
    public byte[] RoutingKey => _identity.Value.RoutingKey;
    public bool IsNewIdentity => false;
    public string ProtectionDescription => "Node app private storage";
    public byte[] Sign(byte[] data) => _node.SignAsync(data).AsTask().GetAwaiter().GetResult();

    private readonly record struct Resolved(string Tag, byte[] PublicKey, byte[] RoutingKey);
}
