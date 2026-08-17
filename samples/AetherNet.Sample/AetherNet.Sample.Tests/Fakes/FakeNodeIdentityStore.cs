// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Sample.Tests.Fakes;

/// <summary>
/// Where a device keeps its one node identity, for the life of a test.
///
/// <para>
/// Sharing one instance between two services is how a test says "these are two apps on the same
/// handset" — they will resolve to the same node, which is the property the whole arrangement exists
/// to provide.
/// </para>
/// </summary>
public sealed class FakeNodeIdentityStore : INodeIdentityStore
{
    private byte[]? _privateKey;

    public FakeNodeIdentityStore() { }

    /// <summary>A device that already holds a known identity — for pinning who a test's node is.</summary>
    public FakeNodeIdentityStore(byte[] privateKey) => _privateKey = privateKey;

    /// <summary>Set to simulate a locked device: the identity is there and cannot be opened.</summary>
    public bool Locked { get; set; }

    /// <summary>How many identities this device has ever minted. Must never exceed one.</summary>
    public int Writes { get; private set; }

    public bool Exists => _privateKey is not null;

    public byte[]? Load()
    {
        if (_privateKey is null) return null;
        if (Locked) throw new NodeIdentityUnavailableException("The device is locked.");
        return _privateKey;
    }

    public void Save(byte[] privateKey)
    {
        _privateKey = privateKey;
        Writes++;
    }

    /// <summary>A node over this device's store — one per app, all resolving to the same identity.</summary>
    public NodeIdentity Node() => new(this);
}
