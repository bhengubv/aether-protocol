// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNetNodeService.Client.Tests;

/// <summary>
/// An app connected to AetherNetService sees the device's identity through the node, and nothing more: it
/// asks for the tag rather than minting, has bytes signed without holding the key, is told "not now" (never
/// "absent") when the node can't be reached, is refused any derived key, and gets the recovery phrase only
/// after the phone itself has confirmed its owner.
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

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default) => Task.FromResult(OutboundResult.Queued);
        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InboundMessage>>(System.Array.Empty<InboundMessage>());
        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default) => Task.FromResult(NodeLinkStatus.Offline);
        public IDisposable Subscribe(IAetherNodeEvents listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }

        public const string Words = "abandon ability able about above absent absorb abstract absurd abuse access accident account accuse achieve acid acoustic acquire across act action actor actress actual";
        public int PhraseAsked;
        public AetherNodeErrorCode? PhraseFails;

        public Task<string> GetRecoveryPhraseAsync(CancellationToken cancellationToken = default)
        {
            PhraseAsked++;
            if (PhraseFails is { } code) throw new AetherNodeException(code, "no");
            return Task.FromResult(Words);
        }
    }

    private sealed class FakeOwner(OwnerCheck answer) : IOwnerCheck
    {
        public string? AskedFor;

        public Task<OwnerCheck> ConfirmAsync(string reason, CancellationToken cancellationToken = default)
        {
            AskedFor = reason;
            return Task.FromResult(answer);
        }
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
    public async Task Backup_asks_the_phone_first_and_then_the_service()
    {
        var service = new FakeNode();
        var owner = new FakeOwner(OwnerCheck.Confirmed);
        var recovery = new NodeClientRecovery(service, owner);

        Assert.Equal(FakeNode.Words, await recovery.ExportRecoveryPhraseAsync());
        Assert.Equal(NodeClientRecovery.Reason, owner.AskedFor);
        Assert.Equal(1, service.PhraseAsked);
    }

    [Theory]
    [InlineData(OwnerCheck.NotConfirmed)]
    [InlineData(OwnerCheck.NoScreenLock)]
    public async Task Without_the_owner_confirmed_the_service_is_asked_nothing(OwnerCheck answer)
    {
        var service = new FakeNode();
        var recovery = new NodeClientRecovery(service, new FakeOwner(answer));

        var refused = await Assert.ThrowsAsync<OwnerNotConfirmedException>(async () => await recovery.ExportRecoveryPhraseAsync());

        Assert.Equal(answer, refused.Outcome);
        Assert.Equal(0, service.PhraseAsked);
    }

    [Fact]
    public async Task A_locked_service_is_not_now_and_a_missing_identity_is_absent()
    {
        var locked = new NodeClientRecovery(new FakeNode { PhraseFails = AetherNodeErrorCode.NodeUnavailable }, new FakeOwner(OwnerCheck.Confirmed));
        var empty = new NodeClientRecovery(new FakeNode { PhraseFails = AetherNodeErrorCode.IdentityAbsent }, new FakeOwner(OwnerCheck.Confirmed));

        await Assert.ThrowsAsync<NodeIdentityUnavailableException>(async () => await locked.ExportRecoveryPhraseAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await empty.ExportRecoveryPhraseAsync());
    }

    [Fact]
    public async Task Restore_says_plainly_it_is_not_here_yet()
    {
        var recovery = new NodeClientRecovery(new FakeNode(), new FakeOwner(OwnerCheck.Confirmed));

        // Not InvalidOperationException: that is the contract's "no identity yet".
        var restore = await Assert.ThrowsAsync<NotSupportedException>(async () => await recovery.RestoreFromPhraseAsync("any phrase"));
        Assert.Equal(NodeClientRecovery.RestoreNotHere, restore.Message);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await recovery.AdoptSeedAsync(new byte[32]));
    }
}
