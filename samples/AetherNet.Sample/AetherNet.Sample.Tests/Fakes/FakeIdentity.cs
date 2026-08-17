// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Sample.Shared.Services;
using AetherNet.Security.Services;

namespace AetherNet.Sample.Tests.Fakes;

/// <summary>
/// A device identity backed by a real keypair, so anything that signs or derives a tag behaves as it
/// would on a phone. The tag can be pinned when a test needs to name who is speaking.
/// </summary>
public sealed class FakeIdentity : IIdentityService
{
    private readonly byte[] _privateKey;

    public FakeIdentity(string? tag = null)
    {
        var (privateKey, publicKey) = Ed25519SigningService.GenerateKeyPair();
        _privateKey = privateKey;
        PublicKey = publicKey;
        RoutingKey = EphemeralRoutingId.DeriveRoutingKey(privateKey);
        AetherTag = tag ?? AetherNetTag.FromPublicKey(publicKey).Value;

        // The node this fake device would answer with — the same identity, so anything signed through
        // the node is signed by the device the rest of the test is talking about.
        Node = new FakeNodeIdentityStore(privateKey).Node();
    }

    /// <summary>This device's node, for anything that signs by asking rather than by holding a key.</summary>
    public INodeIdentity Node { get; }

    public string AetherTag { get; }
    public byte[] PublicKey { get; }
    public byte[] RoutingKey { get; }
    public bool IsNewIdentity => false;
    public string ProtectionDescription => "Test identity";

    /// <summary>
    /// Signs the way the node does — the key is held here and never handed out, which is the property
    /// under test everywhere this fake is used.
    /// </summary>
    public byte[] Sign(byte[] data) => Ed25519SigningService.Sign(_privateKey, data);

    /// <summary>
    /// An identity nobody else in the test run shares. Some services register themselves by tag, so
    /// two rigs claiming the same one collide.
    /// </summary>
    public static FakeIdentity Unique() => new();
}
