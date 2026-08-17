// SPDX-License-Identifier: MIT

using System;
using System.Text;
using System.Threading.Tasks;
using AetherNet.Content;
using AetherNet.Identity;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Cards.Tests;

/// <summary>
/// Publishing a card as the device, without the publisher ever holding the device's key.
///
/// <para>
/// A card is signed by its author so that any holder can re-serve it to a stranger who still verifies
/// it. The author is the device — its AetherTag is what a reader checks against — but that does not
/// mean whoever calls the publisher should be handed the device's private key to do it. Passing a key
/// in order to publish is the same mistake as an app minting its own identity, one layer up: it puts
/// the device's secret in the hands of every component that ever wants to sign something.
/// </para>
///
/// <para>So a card can be published by naming the node as author. The node signs; the key stays put.</para>
/// </summary>
public class CardAuthorshipTests
{
    /// <summary>The device's one identity, for the life of a test.</summary>
    private sealed class MemoryStore : INodeIdentityStore
    {
        private byte[]? _key;

        public MemoryStore() { }

        /// <summary>Start from a known secret, so two publishers can be compared over one key.</summary>
        public MemoryStore(byte[] privateKey) => _key = privateKey;

        public bool Exists => _key is not null;
        public byte[]? Load() => _key;
        public void Save(byte[] privateKey) => _key = privateKey;
    }

    private static CardService Cards() =>
        new(new FakeContentService(),
            new DirectoryService(new CapturingMeshSender("A"), new Ed25519NameBindingVerifier()));

    // ── The device is the author ──────────────────────────────────────────────

    [Fact]
    public async Task A_card_published_by_the_node_is_authored_by_the_device()
    {
        var node = new NodeIdentity(new MemoryStore());

        var card = await Cards().PublishCardAsync(
            "home", Encoding.UTF8.GetBytes("{}"), "application/vnd.aether.card+json", node, 1);

        Assert.Equal(await node.GetPublicKeyAsync(), card.AuthorPublicKey);
    }

    /// <summary>
    /// The property hold-and-forward rests on: a third device that never met the author still verifies
    /// the card against the author's tag.
    /// </summary>
    [Fact]
    public async Task A_card_verifies_against_the_authors_aether_tag()
    {
        var node = new NodeIdentity(new MemoryStore());

        var card = await Cards().PublishCardAsync(
            "home", Encoding.UTF8.GetBytes("{}"), "application/vnd.aether.card+json", node, 1);

        var tag = await node.GetOrMintAsync();
        Assert.True(AetherNetTag.Verify(tag.Value, card.AuthorPublicKey));
    }

    /// <summary>
    /// Both ways of naming an author must produce the same card over the same key, or the additive
    /// overload has quietly forked the wire format.
    /// </summary>
    [Fact]
    public async Task Publishing_by_node_matches_publishing_by_key()
    {
        var (privateKey, _) = Ed25519SigningService.GenerateKeyPair();
        var node = new NodeIdentity(new MemoryStore(privateKey));
        var body = Encoding.UTF8.GetBytes("{\"blocks\":[]}");

        var byNode = await Cards().PublishCardAsync("home", body, "text/plain", node, 1);
        var byKey = await Cards().PublishCardAsync("home", body, "text/plain", privateKey, 1);

        Assert.Equal(byKey.AuthorPublicKey, byNode.AuthorPublicKey);
        Assert.Equal(byKey.Descriptor.RootHash, byNode.Descriptor.RootHash);
        Assert.Equal(byKey.Signature, byNode.Signature);
    }

    // ── Guarding the inputs ───────────────────────────────────────────────────

    [Fact]
    public async Task PublishCardAsync_refuses_a_card_with_no_author() =>
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Cards().PublishCardAsync(
            "home", Encoding.UTF8.GetBytes("{}"), "text/plain", (INodeIdentity)null!, 1));
}
