// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Cards;
using AetherNet.Content;
using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Cards.Tests;

/// <summary>
/// The <c>aether://</c> resolver: <c>aether://&lt;tag&gt;/&lt;name&gt;</c> resolves to the verified card
/// the tag's owner published — including across nodes, which is the mesh-web moment (a card carried by a
/// stranger's device is reachable through its address).
/// </summary>
public class AetherResolverTests
{
    private static (AetherResolver Resolver, CardService Cards, CapturingMeshSender Sender) NewNode()
    {
        var sender = new CapturingMeshSender("node");
        var cards = new CardService(new FakeContentService(), new DirectoryService(sender, new Ed25519NameBindingVerifier()));
        return (new AetherResolver(cards), cards, sender);
    }

    [Fact]
    public async Task Resolve_TagAndName_ReturnsVerifiedCard()
    {
        var (resolver, cards, _) = NewNode();
        var (priv, pub) = Ed25519SigningService.GenerateKeyPair();
        await cards.PublishCardAsync("menu", Encoding.UTF8.GetBytes("<h1>Menu</h1>"), "text/html", priv, 1);

        var tag = AetherNetTag.FromPublicKey(pub).Value;
        var result = await resolver.ResolveAsync($"aether://{tag}/menu");

        var resolved = Assert.IsType<AetherResolution.CardResolved>(result);
        Assert.Equal("menu", resolved.Card.Name);
        Assert.Equal(1, resolved.Card.Version);
        Assert.True(cards.VerifyCard(resolved.Card));
    }

    [Fact]
    public async Task Resolve_WrongTagForSameName_ReturnsNotFound()
    {
        var (resolver, cards, _) = NewNode();
        var (priv, _) = Ed25519SigningService.GenerateKeyPair();
        await cards.PublishCardAsync("menu", Encoding.UTF8.GetBytes("x"), "text/html", priv, 1);

        var (_, strangerPub) = Ed25519SigningService.GenerateKeyPair();
        var strangerTag = AetherNetTag.FromPublicKey(strangerPub).Value;
        var result = await resolver.ResolveAsync($"aether://{strangerTag}/menu", TimeSpan.FromMilliseconds(50));

        Assert.IsType<AetherResolution.NotFound>(result);
    }

    [Fact]
    public async Task Resolve_ContentPath_ReturnsContentTarget()
    {
        var (resolver, _, _) = NewNode();
        var (_, pub) = Ed25519SigningService.GenerateKeyPair();
        var tag = AetherNetTag.FromPublicKey(pub).Value;

        var result = await resolver.ResolveAsync($"aether://{tag}/content/abc123deadbeef");

        var target = Assert.IsType<AetherResolution.ContentTarget>(result);
        Assert.Equal("abc123deadbeef", target.RootHash);
        Assert.Equal(tag, target.Tag);
    }

    [Fact]
    public async Task Resolve_MalformedUri_ReturnsInvalid()
    {
        var (resolver, _, _) = NewNode();
        var result = await resolver.ResolveAsync("http://not-aether/x");
        Assert.IsType<AetherResolution.Invalid>(result);
    }

    [Fact]
    public async Task Resolve_UhidAuthority_ReturnsInvalid()
    {
        var (resolver, _, _) = NewNode();
        var uhid = new string('a', 64); // a valid 64-hex UHID authority, but not a tag
        var result = await resolver.ResolveAsync($"aether://{uhid}/menu", TimeSpan.FromMilliseconds(50));
        Assert.IsType<AetherResolution.Invalid>(result);
    }

    [Fact]
    public async Task TwoNode_CardCarriedThenResolvedByUri()
    {
        var (priv, pub) = Ed25519SigningService.GenerateKeyPair();

        // Node A authors and publishes a card.
        var senderA = new CapturingMeshSender("A");
        var cardsA = new CardService(new FakeContentService(), new DirectoryService(senderA, new Ed25519NameBindingVerifier()));
        await cardsA.PublishCardAsync("flyer", Encoding.UTF8.GetBytes("<p>Yard sale Saturday</p>"), "text/html", priv, 1);

        // Node B carries A's signed name binding (data-mule hop).
        var dirB = new DirectoryService(new CapturingMeshSender("B"), new Ed25519NameBindingVerifier());
        var cardsB = new CardService(new FakeContentService(), dirB);
        var resolverB = new AetherResolver(cardsB);

        var namePublish = senderA.Broadcasts.First(p => p.Type == PacketType.NamePublish);
        namePublish.SourceUhid = "A";
        await dirB.HandleAsync(namePublish);

        // B reaches the card purely through its aether:// address.
        var tag = AetherNetTag.FromPublicKey(pub).Value;
        var result = await resolverB.ResolveAsync($"aether://{tag}/flyer", TimeSpan.FromMilliseconds(100));

        var resolved = Assert.IsType<AetherResolution.CardResolved>(result);
        Assert.Equal("flyer", resolved.Card.Name);
        Assert.Equal(1, resolved.Card.Version);
        Assert.True(cardsB.VerifyCard(resolved.Card));
    }

    [Fact]
    public async Task Squatter_CannotBlockOwnerResolution()
    {
        var (ownerPriv, ownerPub) = Ed25519SigningService.GenerateKeyPair();
        var (squatterPriv, _) = Ed25519SigningService.GenerateKeyPair();

        // A squatter publishes a card named "menu" (version 99, trying to look "newest")...
        var squatterSender = new CapturingMeshSender("squatter");
        var squatterCards = new CardService(new FakeContentService(), new DirectoryService(squatterSender, new Ed25519NameBindingVerifier()));
        await squatterCards.PublishCardAsync("menu", Encoding.UTF8.GetBytes("<script>evil()</script>"), "text/html", squatterPriv, 99);

        // ...and the real owner publishes their own "menu" (version 1).
        var ownerSender = new CapturingMeshSender("owner");
        var ownerCards = new CardService(new FakeContentService(), new DirectoryService(ownerSender, new Ed25519NameBindingVerifier()));
        var ownerCard = await ownerCards.PublishCardAsync("menu", Encoding.UTF8.GetBytes("<h1>Owner's menu</h1>"), "text/html", ownerPriv, 1);

        // A third node carries BOTH publishes, then resolves the owner's address.
        var dirC = new DirectoryService(new CapturingMeshSender("C"), new Ed25519NameBindingVerifier());
        var resolverC = new AetherResolver(new CardService(new FakeContentService(), dirC));
        await dirC.HandleAsync(squatterSender.Broadcasts.First(p => p.Type == PacketType.NamePublish));
        await dirC.HandleAsync(ownerSender.Broadcasts.First(p => p.Type == PacketType.NamePublish));

        var ownerTag = AetherNetTag.FromPublicKey(ownerPub).Value;
        var result = await resolverC.ResolveAsync($"aether://{ownerTag}/menu", TimeSpan.FromMilliseconds(100));

        // The owner's address resolves to the OWNER's content — the squatter's higher version is
        // irrelevant because it lives in the squatter's own scope slot, not the owner's.
        var resolved = Assert.IsType<AetherResolution.CardResolved>(result);
        Assert.Equal(ownerCard.Descriptor.RootHash, resolved.Card.Descriptor.RootHash);
        Assert.Equal(1, resolved.Card.Version);
    }
}
