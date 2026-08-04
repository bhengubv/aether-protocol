// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Content;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Cards.Tests;

/// <summary>
/// End-to-end card publish / resolve / verify, including the two-node "publish → carry → fetch →
/// verify" convergence the card economy relies on.
/// </summary>
public class CardServiceTests
{
    private static CardService NewNode(out CapturingMeshSender sender, out FakeContentService content)
    {
        sender = new CapturingMeshSender("node");
        content = new FakeContentService();
        var directory = new DirectoryService(sender, new Ed25519NameBindingVerifier());
        return new CardService(content, directory);
    }

    [Fact]
    public async Task PublishThenResolve_SameNode_RoundTripsAndVerifies()
    {
        var cards = NewNode(out _, out var content);
        var (priv, _) = Ed25519SigningService.GenerateKeyPair();

        var body = Encoding.UTF8.GetBytes("<h1>Today's specials</h1>");
        var card = await cards.PublishCardAsync("menu", body, "text/html", priv, 1);
        Assert.True(cards.VerifyCard(card));

        var resolved = await cards.ResolveCardAsync(card.AuthorPublicKey, "menu");
        Assert.NotNull(resolved);
        Assert.Equal(card.Descriptor.RootHash, resolved!.Descriptor.RootHash);
        Assert.Equal(1, resolved.Version);
        Assert.True(cards.VerifyCard(resolved));

        // The content-addressed bytes assemble back byte-for-byte.
        var fetched = await content.AssembleAsync(resolved.Descriptor.RootHash);
        Assert.Equal(body, fetched);
    }

    [Fact]
    public async Task VerifyCard_RejectsTamperedSignatureOrVersion()
    {
        var cards = NewNode(out _, out _);
        var (priv, _) = Ed25519SigningService.GenerateKeyPair();
        var card = await cards.PublishCardAsync("menu", Encoding.UTF8.GetBytes("x"), "text/html", priv, 1);

        var badSignature = (byte[])card.Signature.Clone();
        badSignature[0] ^= 0xFF;
        Assert.False(cards.VerifyCard(card with { Signature = badSignature }));

        // Version is bound into the signed body, so a swapped version fails verification.
        Assert.False(cards.VerifyCard(card with { Version = card.Version + 1 }));
    }

    [Fact]
    public async Task TwoNode_PublishCarryFetchVerify_Converges()
    {
        var (priv, pub) = Ed25519SigningService.GenerateKeyPair();

        // Node A authors and publishes a card.
        var senderA = new CapturingMeshSender("A");
        var cardsA = new CardService(new FakeContentService(), new DirectoryService(senderA, new Ed25519NameBindingVerifier()));
        var card = await cardsA.PublishCardAsync("flyer", Encoding.UTF8.GetBytes("<p>Yard sale Saturday</p>"), "text/html", priv, 1);

        // Node B carries A's signed name binding (data-mule hop) and resolves it.
        var dirB = new DirectoryService(new CapturingMeshSender("B"), new Ed25519NameBindingVerifier());
        var cardsB = new CardService(new FakeContentService(), dirB);

        var namePublish = senderA.Broadcasts.First(p => p.Type == PacketType.NamePublish);
        namePublish.SourceUhid = "A";
        await dirB.HandleAsync(namePublish);

        var resolved = await cardsB.ResolveCardAsync(pub, "flyer", TimeSpan.FromMilliseconds(100));
        Assert.NotNull(resolved);
        Assert.Equal(card.Descriptor.RootHash, resolved!.Descriptor.RootHash);
        Assert.Equal(1, resolved.Version);
        Assert.True(cardsB.VerifyCard(resolved));
    }

    [Fact]
    public async Task ResolveCard_ByWrongAuthor_ReturnsNull()
    {
        var cards = NewNode(out _, out _);
        var (priv, _) = Ed25519SigningService.GenerateKeyPair();
        await cards.PublishCardAsync("menu", Encoding.UTF8.GetBytes("x"), "text/html", priv, 1);

        var (_, strangerPub) = Ed25519SigningService.GenerateKeyPair();
        var resolved = await cards.ResolveCardAsync(strangerPub, "menu", TimeSpan.FromMilliseconds(50));
        Assert.Null(resolved);
    }
}
