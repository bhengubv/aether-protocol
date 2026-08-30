// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Named sets of cards, in an order somebody chose.
///
/// <para>
/// Cards arrive one at a time and land in a heap sorted by when they turned up, which is the order
/// they were least likely to be wanted in. A deck is the other thing people do with cards: gather the
/// ones that go together, put them in an order, name it, and hand the whole lot over.
/// </para>
/// </summary>
public class CardDeckTests
{
    private static Decks APhone() => new(new InMemoryCardStore());

    private const string Plumber = "aether://Y6TK9-EW9KK/plumber";

    private const string Tiler = "aether://KXJB7-MN2P4/tiler";

    private const string Sparks = "aether://Q3WRT-88ZZA/sparks";

    private static Decks WithTrades()
    {
        var decks = APhone();
        decks.Make("Trades");
        decks.Add("Trades", Plumber);
        decks.Add("Trades", Tiler);
        decks.Add("Trades", Sparks);
        return decks;
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_deck_is_made_by_naming_it()
    {
        var decks = APhone();

        Assert.NotNull(decks.Make("Trades"));
        Assert.Equal("Trades", Assert.Single(decks.All).Name);
    }

    [Fact]
    public void Naming_it_the_same_thing_twice_is_the_same_deck()
    {
        var decks = APhone();
        decks.Make("Trades");
        decks.Make("trades");

        Assert.Single(decks.All);
    }

    [Fact]
    public void A_deck_with_no_name_is_not_a_deck()
    {
        var decks = APhone();

        Assert.Null(decks.Make("   "));
        Assert.Null(decks.Make(null));
        Assert.Empty(decks.All);
    }

    [Fact]
    public void It_can_be_called_something_else()
    {
        var decks = WithTrades();

        Assert.True(decks.Rename("Trades", "People who fix things"));
        Assert.Equal("People who fix things", Assert.Single(decks.All).Name);
        Assert.Equal(3, decks.Get("People who fix things")!.Cards.Count);
    }

    /// <summary>
    /// But not to a name already taken.
    /// </summary>
    /// <remarks>
    /// Merging them would lose whichever order somebody had put the second one in, and they would not
    /// find out until they went looking for it.
    /// </remarks>
    [Fact]
    public void It_cannot_take_a_name_that_is_already_taken()
    {
        var decks = WithTrades();
        decks.Make("Street");

        Assert.False(decks.Rename("Street", "Trades"));
        Assert.Equal(2, decks.All.Count);
    }

    // ── Order ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Cards_stay_in_the_order_they_were_put()
    {
        Assert.Equal([Plumber, Tiler, Sparks], WithTrades().Get("Trades")!.Cards);
    }

    [Fact]
    public void A_card_can_be_moved_inside_its_deck()
    {
        var decks = WithTrades();

        Assert.True(decks.MoveCard("Trades", Sparks, -2));
        Assert.Equal([Sparks, Plumber, Tiler], decks.Get("Trades")!.Cards);
    }

    /// <summary>Moving past the end stops at the end rather than refusing.</summary>
    /// <remarks>
    /// Somebody tapping the down arrow on the last card has said something perfectly clear, and the
    /// honest answer is that it is already last.
    /// </remarks>
    [Fact]
    public void Moving_past_the_end_stops_at_the_end()
    {
        var decks = WithTrades();

        Assert.True(decks.MoveCard("Trades", Plumber, 9));
        Assert.Equal([Tiler, Sparks, Plumber], decks.Get("Trades")!.Cards);
        Assert.False(decks.MoveCard("Trades", Plumber, 9));
    }

    [Fact]
    public void The_decks_themselves_can_be_ordered()
    {
        var decks = WithTrades();
        decks.Make("Street");
        decks.Make("Family");

        Assert.True(decks.Move("Family", -2));
        Assert.Equal(["Family", "Trades", "Street"], decks.All.Select(d => d.Name));
    }

    // ── What is in one ────────────────────────────────────────────────────────

    [Fact]
    public void The_same_card_is_not_put_in_twice()
    {
        var decks = WithTrades();

        Assert.False(decks.Add("Trades", Plumber));
        Assert.Equal(3, decks.Get("Trades")!.Cards.Count);
    }

    /// <summary>
    /// A card can be in as many decks as somebody likes.
    /// </summary>
    /// <remarks>
    /// A deck holds addresses, not copies — so the plumber can be in Trades and in People On My Street
    /// without the phone holding two of him.
    /// </remarks>
    [Fact]
    public void A_card_can_be_in_more_than_one_deck()
    {
        var decks = WithTrades();
        decks.Make("Street");

        Assert.True(decks.Add("Street", Plumber));
        Assert.Contains(Plumber, decks.Get("Trades")!.Cards);
        Assert.Contains(Plumber, decks.Get("Street")!.Cards);
    }

    /// <summary>Taking a card out of a deck does not take it off the phone.</summary>
    [Fact]
    public void Taking_a_card_out_leaves_it_on_the_phone()
    {
        var decks = WithTrades();
        decks.Make("Street");
        decks.Add("Street", Plumber);

        Assert.True(decks.Remove("Trades", Plumber));
        Assert.DoesNotContain(Plumber, decks.Get("Trades")!.Cards);
        Assert.Contains(Plumber, decks.Get("Street")!.Cards);
    }

    /// <summary>And putting a deck away does not either.</summary>
    [Fact]
    public void Putting_a_deck_away_is_not_throwing_the_cards_away()
    {
        var store = new InMemoryCardStore();
        var decks = new Decks(store);
        decks.Make("Trades");
        decks.Add("Trades", Plumber);

        Assert.True(decks.Drop("Trades"));
        Assert.Empty(decks.All);

        // The deck was a list. The cards are held by the phone, not by it.
        Assert.Empty(store.GetHeldCards());
    }

    // ── It survives ───────────────────────────────────────────────────────────

    [Fact]
    public void Decks_are_still_there_after_a_restart()
    {
        var store = new InMemoryCardStore();

        var before = new Decks(store);
        before.Make("Trades");
        before.Add("Trades", Plumber);
        before.Add("Trades", Tiler);
        before.MoveCard("Trades", Tiler, -1);

        var after = new Decks(store);

        Assert.Equal([Tiler, Plumber], after.Get("Trades")!.Cards);
    }

    /// <summary>Nonsense in the store leaves a phone with no decks, not a phone that will not open.</summary>
    [Fact]
    public void Rubbish_in_the_store_is_survivable()
    {
        var store = new InMemoryCardStore();
        store.SetDecks("{not json at all");

        Assert.Empty(new Decks(store).All);
    }

    // ── Limits ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_name_is_trimmed_rather_than_refused()
    {
        var decks = APhone();
        var made = decks.Make(new string('x', Decks.LongestName + 40));

        Assert.Equal(Decks.LongestName, made!.Name.Length);
    }

    [Fact]
    public void There_is_a_limit_on_how_many_decks_one_phone_keeps()
    {
        var decks = APhone();

        for (var i = 0; i < Decks.Most + 5; i++) decks.Make($"Deck {i}");

        Assert.Equal(Decks.Most, decks.All.Count);
    }
}
