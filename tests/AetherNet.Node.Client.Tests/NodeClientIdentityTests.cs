// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNet.Node.Client.Tests;

/// <summary>
/// An app connected to AetherNetService sees the device's identity through the node, and nothing more: it
/// asks for the tag rather than minting, has bytes signed without holding the key, is told "not now" (never
/// "absent") when the node can't be reached, and is refused any derived key or recovery phrase.
/// </summary>
public class NodeClientIdentityTests
{
    private static readonly AetherNetTag NodeTag = Parse("9BWNJ-QPXG8");

    private static AetherNetTag Parse(string value)
    {
        Assert.True(AetherNetTag.TryParse(value, out var tag));
        return tag;
    }

    private sealed class FakeNode : IAetherNodeClient
    {
        public bool Unavailable;
        public byte[]? SignedData;

        private void ThrowIfUnavailable()
        {
            if (Unavailable)
            {
                throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService is not installed on this phone");
            }
        }

        public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfUnavailable();
            return Task.FromResult(NodeTag);
        }

        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfUnavailable();
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            ThrowIfUnavailable();
            SignedData = data.ToArray();
            return Task.FromResult(new byte[] { 9, 9 });
        }

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => Task.FromResult(OutboundResult.Queued);
        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InboundMessage>>(System.Array.Empty<InboundMessage>());
        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default) => Task.FromResult(NodeLinkStatus.Offline);
        public IDisposable Subscribe(IAetherNodeEvents listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task The_tag_is_whatever_the_node_says_it_is()
    {
        var identity = new NodeClientIdentity(new FakeNode());

        Assert.Equal(NodeTag, await identity.GetOrMintAsync());
    }

    [Fact]
    public async Task Signing_hands_the_bytes_to_the_node_and_returns_its_signature()
    {
        var node = new FakeNode();
        var identity = new NodeClientIdentity(node);

        var signature = await identity.SignAsync(new byte[] { 4, 5, 6 });

        Assert.Equal(new byte[] { 4, 5, 6 }, node.SignedData);
        Assert.Equal(new byte[] { 9, 9 }, signature);
    }

    [Fact]
    public async Task A_node_that_cannot_be_reached_is_not_now_never_absent()
    {
        var identity = new NodeClientIdentity(new FakeNode { Unavailable = true });

        // NodeIdentityUnavailableException is INodeIdentity's "not now" — never a reason to mint a replacement.
        await Assert.ThrowsAsync<NodeIdentityUnavailableException>(async () => await identity.GetOrMintAsync());
        await Assert.ThrowsAsync<NodeIdentityUnavailableException>(async () => await identity.GetPublicKeyAsync());
        await Assert.ThrowsAsync<NodeIdentityUnavailableException>(async () => await identity.SignAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task A_derived_key_never_leaves_the_node()
    {
        var identity = new NodeClientIdentity(new FakeNode());

        await Assert.ThrowsAsync<NotSupportedException>(async () => await identity.DeriveKeyAsync("erid-routing"));
    }

    [Fact]
    public async Task Backup_and_restore_say_plainly_they_are_not_here()
    {
        var recovery = new NodeClientRecovery();

        var export = await Assert.ThrowsAsync<InvalidOperationException>(async () => await recovery.ExportRecoveryPhraseAsync());
        Assert.Equal(NodeClientRecovery.NotHere, export.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recovery.RestoreFromPhraseAsync("any phrase"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recovery.AdoptSeedAsync(new byte[32]));
    }
}
