// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using System.Text;
using AetherNet.Content;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The baseball-card economy: a card is <b>held, kept offline, and re-served</b>.
///
/// <para>
/// This is the property that separates AetherNet's mesh-web from every other way of putting a page on
/// the internet. A page is somewhere you go; a card is something you have. Once somebody hands you
/// one it is yours — it opens with no signal, with its author asleep, unreachable or gone, and it
/// opens the same way next year. And you can hand it to a third person who has never met the author,
/// who still checks it against <i>the author's</i> key and hashes rather than against yours.
/// </para>
///
/// <para>
/// That last part is what lets spread come unstuck from origin, and it is the whole reason a card is
/// a signed, content-addressed object rather than a URL. Passing one on proves nothing about whoever
/// passed it and everything about whoever wrote it — so accepting a card from a stranger is safe, and
/// a card can travel further than its author ever did.
/// </para>
///
/// <para>
/// Every failure here is silent in the ordinary way. Holding that lasts until the app restarts still
/// looks like holding. A holder that cannot answer for what it holds looks exactly like a peer that
/// is out of range. So these are the tests that have to exist.
/// </para>
/// </summary>
public class CardTradingTests : IDisposable
{
    private readonly List<string> _files = [];

    /// <summary>A store on disk, because ":memory:" does not survive being reopened — and neither would the point.</summary>
    private AetherStore ADisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-deck-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return new AetherStore(path);
    }

    private sealed class Phone
    {
        public FakeIdentity Me { get; } = FakeIdentity.Unique();
        public FakeRadioMesh Radio { get; }
        public MeshWebService Web { get; }
        public MyPages Pages { get; }
        public Deck Deck { get; }

        public IContentStore Content { get; }

        /// <param name="content">
        ///   Handed in so a "restart" can keep it. On a real phone the content store is SQLite and
        ///   survives; a test that threw the chunks away every time would be modelling a device that
        ///   forgets its own files, and would prove the opposite of what it set out to.
        /// </param>
        public Phone(AetherStore store, IContentStore? content = null)
        {
            Radio = new FakeRadioMesh(Me.AetherTag);
            Pages = new MyPages(new AetherStoreCardStore(store));
            Deck = new Deck(new AetherStoreCardStore(store));
            Content = content ?? new InMemoryContentStore();
            Web = new MeshWebService(Me.Node, Content, new RadioMeshLink(Radio), null, Pages, Deck);
        }

        public string Tag => Me.AetherTag;
    }

    private static void Link(Phone a, Phone b)
    {
        a.Radio.Peer = b.Radio;
        b.Radio.Peer = a.Radio;
        a.Radio.Link();
        b.Radio.Link();
    }

    private static void Part(Phone a, Phone b)
    {
        a.Radio.Unlink();
        b.Radio.Unlink();
        a.Radio.Peer = null;
        b.Radio.Peer = null;
    }

    private static async Task WritesAsync(Phone phone, string name, string title)
    {
        phone.Pages.Save(new WebCard
        {
            Name = name,
            Doc = new CardDocument
            {
                Title = title,
                Blocks = [CardBlock.Of(CardBlock.Text, $"Written by whoever owns {phone.Tag}.")],
            },
        });

        await phone.Web.PublishAsync(name);
    }

    // ── Held ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_somebodys_card_is_how_you_come_to_hold_it()
    {
        using var mine = ADisk();
        using var theirs = ADisk();
        var me = new Phone(mine);
        var them = new Phone(theirs);
        Link(me, them);

        await me.Web.EnsureReadyAsync();
        await them.Web.EnsureReadyAsync();
        await WritesAsync(them, "shop", "The shop");

        await me.Web.OpenAsync(them.Web.Address("shop"));

        var held = Assert.Single(me.Deck.All, c => c.Name == "shop");
        Assert.Equal(them.Tag, held.AuthorTag);
        Assert.Equal("The shop", held.Title);
    }

    /// <summary>
    /// You do not hold your own cards. You have them.
    /// </summary>
    [Fact]
    public async Task Your_own_pages_are_not_in_your_deck()
    {
        using var store = ADisk();
        var me = new Phone(store);
        await me.Web.EnsureReadyAsync();

        await me.Web.OpenAsync(me.Web.HomeAddress);

        Assert.Empty(me.Deck.All);
    }

    /// <summary>
    /// <b>The one that was broken.</b> A deck in memory means "held offline forever" lasts until the
    /// app is next closed — which looks exactly like holding right up until it does not.
    /// </summary>
    [Fact]
    public async Task A_held_card_is_still_held_after_the_app_is_closed()
    {
        using var mine = ADisk();
        using var theirs = ADisk();
        var them = new Phone(theirs);

        var me = new Phone(mine);
        Link(me, them);
        await me.Web.EnsureReadyAsync();
        await them.Web.EnsureReadyAsync();
        await WritesAsync(them, "shop", "The shop");
        await me.Web.OpenAsync(them.Web.Address("shop"));

        // The app closes and opens again. Same database, everything else new.
        var later = new Deck(new AetherStoreCardStore(mine));

        Assert.Contains(later.All, c => c.Name == "shop" && c.AuthorTag == them.Tag);
    }

    [Fact]
    public async Task A_held_card_carries_everything_needed_to_check_it_without_its_author()
    {
        using var mine = ADisk();
        using var theirs = ADisk();
        var me = new Phone(mine);
        var them = new Phone(theirs);
        Link(me, them);
        await me.Web.EnsureReadyAsync();
        await them.Web.EnsureReadyAsync();
        await WritesAsync(them, "shop", "The shop");

        await me.Web.OpenAsync(them.Web.Address("shop"));
        var held = Assert.Single(me.Deck.All, c => c.Name == "shop");

        Assert.NotEmpty(held.AuthorKey);
        Assert.NotEmpty(held.Signature);
        Assert.NotEmpty(held.RootHash);
        Assert.NotNull(Deck.DescriptorOf(held));
        Assert.Equal(held.RootHash, Deck.DescriptorOf(held)!.RootHash);
    }

    /// <summary>
    /// A descriptor that does not describe the card it is filed under is refused rather than used.
    /// Storage that has gone bad must not become content that looks fine.
    /// </summary>
    [Fact]
    public void A_held_card_whose_manifest_does_not_match_it_cannot_be_served()
    {
        var wrong = new HeldCard(
            Address: "aether://TAG/shop",
            AuthorTag: "TAG",
            AuthorKey: [1, 2, 3],
            Name: "shop",
            Title: "The shop",
            Version: 1,
            RootHash: "abc123",
            Signature: [4, 5, 6],
            Descriptor: """{"RootHash":"something-else","ChunkHashes":[]}""",
            GotMs: 0,
            GotFrom: "");

        Assert.Null(Deck.DescriptorOf(wrong));
    }

    [Fact]
    public void A_held_card_that_cannot_be_read_at_all_cannot_be_served()
    {
        var broken = new HeldCard(
            "aether://TAG/shop", "TAG", [1], "shop", "", 1, "abc", [2],
            Descriptor: "{ not json", GotMs: 0, GotFrom: "");

        Assert.Null(Deck.DescriptorOf(broken));
    }

    // ── Kept offline ──────────────────────────────────────────────────────────

    /// <summary>
    /// The author is gone — not asleep, not out of range, gone — and the card still opens.
    /// </summary>
    [Fact]
    public async Task A_held_card_opens_with_no_radio_and_no_author()
    {
        using var mine = ADisk();
        using var theirs = ADisk();
        var me = new Phone(mine);
        var them = new Phone(theirs);
        Link(me, them);
        await me.Web.EnsureReadyAsync();
        await them.Web.EnsureReadyAsync();
        await WritesAsync(them, "shop", "The shop");

        var address = them.Web.Address("shop");
        await me.Web.OpenAsync(address);
        Part(me, them);

        var page = await me.Web.OpenAsync(address);

        Assert.True(page.Ok, page.Error);
        Assert.Equal("The shop", page.Card!.Title);
        Assert.False(page.Remote);
    }

    // ── Re-served ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Hold-and-forward — the test the whole model exists for.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three phones. The author writes a card and hands it to a friend. The author then goes away
    /// entirely — no link, no route, nothing. A stranger who has never met the author, and never
    /// will, meets only the friend, opens the author's address, and gets the card.
    /// </para>
    /// <para>
    /// The stranger verifies it against the author's key and the descriptor's hashes, so the friend
    /// could not have altered a word of it in passing. Spread has come unstuck from origin: the card
    /// travels further than its author ever did, and nobody had to be trusted for that to be safe.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_stranger_gets_the_card_from_a_holder_who_did_not_write_it()
    {
        using var authorDisk = ADisk();
        using var friendDisk = ADisk();
        using var strangerDisk = ADisk();

        var author = new Phone(authorDisk);
        var friend = new Phone(friendDisk);
        var stranger = new Phone(strangerDisk);

        Link(author, friend);
        await author.Web.EnsureReadyAsync();
        await friend.Web.EnsureReadyAsync();
        await WritesAsync(author, "shop", "Kagiso Plumbing");

        var address = author.Web.Address("shop");
        Assert.True((await friend.Web.OpenAsync(address)).Ok);

        // The author leaves. Not asleep — gone.
        Part(author, friend);

        // The stranger has never met the author and never will.
        Link(friend, stranger);
        await stranger.Web.EnsureReadyAsync();

        var page = await stranger.Web.OpenAsync(address);

        Assert.True(page.Ok, page.Error);
        Assert.Equal("Kagiso Plumbing", page.Card!.Title);

        // Signed by the author, not by the phone that handed it over.
        Assert.Equal(author.Tag, page.AuthorTag);
        Assert.NotEqual(friend.Tag, page.AuthorTag);

        // And the stranger now holds it too, so it can go one hop further.
        Assert.Contains(stranger.Deck.All, c => c.AuthorTag == author.Tag && c.Name == "shop");
    }

    /// <summary>
    /// And a holder can still do it after being closed and reopened — the bindings it admitted lived
    /// only in memory, so without re-offering them on waking, a restart quietly turned a holder back
    /// into a bystander.
    /// </summary>
    [Fact]
    public async Task A_holder_can_still_serve_after_being_closed_and_reopened()
    {
        using var authorDisk = ADisk();
        using var friendDisk = ADisk();
        using var strangerDisk = ADisk();

        var author = new Phone(authorDisk);
        var friend = new Phone(friendDisk);

        Link(author, friend);
        await author.Web.EnsureReadyAsync();
        await friend.Web.EnsureReadyAsync();
        await WritesAsync(author, "shop", "Kagiso Plumbing");

        var address = author.Web.Address("shop");
        Assert.True((await friend.Web.OpenAsync(address)).Ok);
        Part(author, friend);

        // The friend's app closes and opens again — same database, same files, everything else new.
        var reopened = new Phone(friendDisk, friend.Content);
        var stranger = new Phone(strangerDisk);
        Link(reopened, stranger);
        await reopened.Web.EnsureReadyAsync();
        await stranger.Web.EnsureReadyAsync();

        var page = await stranger.Web.OpenAsync(address);

        Assert.True(page.Ok, page.Error);
        Assert.Equal(author.Tag, page.AuthorTag);
    }

    // ── Handing one over ──────────────────────────────────────────────────────

    [Fact]
    public async Task Giving_a_card_offers_it_to_whoever_is_linked()
    {
        using var mine = ADisk();
        using var theirs = ADisk();
        var me = new Phone(mine);
        var them = new Phone(theirs);
        Link(me, them);
        await me.Web.EnsureReadyAsync();
        await them.Web.EnsureReadyAsync();
        await WritesAsync(me, "shop", "The shop");

        string? offered = null;
        them.Web.Offered += a => offered = a;

        Assert.True(await me.Web.GiveAsync(me.Web.Address("shop")));
        Assert.Equal(me.Web.Address("shop"), offered);
    }

    [Fact]
    public async Task Giving_a_card_nobody_is_near_enough_to_receive_says_so()
    {
        using var store = ADisk();
        var me = new Phone(store);
        await me.Web.EnsureReadyAsync();
        await WritesAsync(me, "shop", "The shop");

        Assert.False(await me.Web.GiveAsync(me.Web.Address("shop")));
    }

    [Fact]
    public async Task Giving_a_card_that_does_not_exist_says_so()
    {
        using var mine = ADisk();
        using var theirs = ADisk();
        var me = new Phone(mine);
        var them = new Phone(theirs);
        Link(me, them);
        await me.Web.EnsureReadyAsync();

        Assert.False(await me.Web.GiveAsync("aether://NOBODY/nothing"));
    }

    /// <summary>
    /// An offer is a reason to look, never a reason to believe. What arrives on the wire is an
    /// address; the card behind it is fetched and checked like anything else.
    /// </summary>
    [Fact]
    public async Task An_offer_that_is_not_a_mesh_address_is_ignored()
    {
        using var mine = ADisk();
        using var theirs = ADisk();
        var me = new Phone(mine);
        var them = new Phone(theirs);
        Link(me, them);
        await me.Web.EnsureReadyAsync();
        await them.Web.EnsureReadyAsync();

        var offers = 0;
        them.Web.Offered += _ => offers++;

        await me.Web.GiveAsync("https://example.invalid/x");

        Assert.Equal(0, offers);
    }

    // ── Letting one go ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_card_can_be_let_go_of()
    {
        using var mine = ADisk();
        using var theirs = ADisk();
        var me = new Phone(mine);
        var them = new Phone(theirs);
        Link(me, them);
        await me.Web.EnsureReadyAsync();
        await them.Web.EnsureReadyAsync();
        await WritesAsync(them, "shop", "The shop");

        var address = them.Web.Address("shop");
        await me.Web.OpenAsync(address);

        Assert.True(me.Deck.Drop(address));
        Assert.False(me.Deck.Holds(address));
    }

    // ── Corrects itself ───────────────────────────────────────────────────────

    /// <summary>
    /// A baseball card that corrects itself when it meets its own author: the copy you hold is the
    /// one you were given, until a newer one from the same author reaches you.
    /// </summary>
    [Fact]
    public void A_newer_version_replaces_a_held_card()
    {
        using var store = ADisk();
        var deck = new Deck(new AetherStoreCardStore(store));

        store.HoldCard(Held(version: 1, title: "Old hours"));
        store.HoldCard(Held(version: 2, title: "New hours"));

        var held = Assert.Single(deck.All);
        Assert.Equal(2, held.Version);
        Assert.Equal("New hours", held.Title);
    }

    /// <summary>
    /// And an older copy arriving afterwards cannot undo it — otherwise a card could be rolled back
    /// by anybody still holding a stale one, which is every holder that has been out of range.
    /// </summary>
    [Fact]
    public void An_older_version_cannot_undo_a_newer_one()
    {
        using var store = ADisk();
        var deck = new Deck(new AetherStoreCardStore(store));

        store.HoldCard(Held(version: 5, title: "Current"));
        store.HoldCard(Held(version: 2, title: "Stale"));

        Assert.Equal("Current", Assert.Single(deck.All).Title);
    }

    private static HeldCard Held(long version, string title) => new(
        Address: "aether://TAG/shop",
        AuthorTag: "TAG",
        AuthorKey: [1, 2, 3],
        Name: "shop",
        Title: title,
        Version: version,
        RootHash: "root",
        Signature: [4, 5, 6],
        Descriptor: """{"RootHash":"root"}""",
        GotMs: version,
        GotFrom: "");

    public void Dispose()
    {
        foreach (var file in _files)
            try { File.Delete(file); } catch (IOException) { }
    }
}
