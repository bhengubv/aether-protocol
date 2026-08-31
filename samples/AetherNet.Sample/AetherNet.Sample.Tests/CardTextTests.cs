// SPDX-License-Identifier: MIT

using System.Linq;
using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A card is a document you write, and writing it back out gives you the same card.
/// </summary>
/// <remarks>
/// <para>
/// The block model is right for the wire and wrong for the hands. The editor made that mistake for
/// months: a panel per block, a labelled blank per field, and two real pages that went a hundred
/// versions through it without ever being written — while the only card in the library that looks
/// like anything was authored in C#, in a test file, by the person who built the form.
/// </para>
/// <para>
/// So the round trip is the whole property. If <c>sketchbook</c> survives it, then the card that was
/// hand-written in a programming language can be opened as a document, changed, and handed on — which
/// is the thing MySpace actually taught and the thing a create-new wizard cannot do.
/// </para>
/// </remarks>
public class CardTextTests
{
    private static string Json(CardDocument card) => card.ToJson();

    // ── The one that matters ────────────────────────────────────────────────────

    [Fact]
    public void The_hand_built_card_survives_being_opened_as_a_document()
    {
        var built = Sketchbook.Built();

        var written = CardText.From(built);
        var back = CardText.Read(written, built);

        Assert.Equal(Json(Speaking(built)), Json(back));
    }

    /// <summary>
    /// The same card with its husks gone — the blocks that hold nothing anybody can read.
    /// </summary>
    /// <remarks>
    /// Opening a card as a document is also what clears them, because a block with no words has no
    /// line to type. Sketchbook carries an empty title block; a template ships "Where =" and
    /// "Reach me =" with nothing after the equals. Those are the reason a page can be a hundred
    /// versions old and still say nothing, and they do not survive being written out.
    /// </remarks>
    private static CardDocument Speaking(CardDocument card) => new()
    {
        Version = card.Version,
        Title = card.Title,
        Blocks = [.. card.Blocks.Where(b =>
            b.Kind == CardBlock.Rule
            || b.ContentHash is { Length: > 0 }
            || b.Value is { Length: > 0 }
            || b.Items is { Count: > 0 })],
    };

    [Fact]
    public void A_block_with_nothing_in_it_does_not_come_back()
    {
        var card = new CardDocument();
        card.Blocks.Add(new CardBlock { Kind = CardBlock.Heading, Value = "What I do" });
        card.Blocks.Add(new CardBlock { Kind = CardBlock.Text, Value = "" });
        card.Blocks.Add(new CardBlock { Kind = CardBlock.KeyValue, Value = "" });

        var back = CardText.Read(CardText.From(card), card);

        var block = Assert.Single(back.Blocks);
        Assert.Equal(CardBlock.Heading, block.Kind);
    }

    [Fact]
    public void A_rule_is_allowed_to_be_empty_because_that_is_what_it_is()
    {
        var card = new CardDocument();
        card.Blocks.Add(new CardBlock { Kind = CardBlock.Rule, Value = "" });

        var back = CardText.Read(CardText.From(card), card);

        Assert.Equal(CardBlock.Rule, Assert.Single(back.Blocks).Kind);
    }

    [Fact]
    public void The_document_for_the_hand_built_card_is_something_a_person_could_have_typed()
    {
        var written = CardText.From(Sketchbook.Built());

        Assert.NotEmpty(written);
        Assert.DoesNotContain("\"k\":", written);      // not JSON wearing a hat
        Assert.DoesNotContain("<", written);           // and not markup either
        Assert.Contains("# ", written);
    }

    // ── Every kind, one at a time ───────────────────────────────────────────────

    [Theory]
    [InlineData(CardBlock.Title, "# Tabang Bhengu")]
    [InlineData(CardBlock.Heading, "## What I do")]
    [InlineData(CardBlock.Eyebrow, "### Hours")]
    [InlineData(CardBlock.Text, "A paragraph with **bold** and a [link](aether://KXJB7-MN2P4/me).")]
    [InlineData(CardBlock.Quote, "> Somebody else said this.")]
    [InlineData(CardBlock.Tip, "!! Worth knowing.")]
    [InlineData(CardBlock.KeyValue, "Open = 07:00 to 17:00")]
    public void A_line_becomes_the_block_it_looks_like(string kind, string line)
    {
        var card = CardText.Read(line);

        var block = Assert.Single(card.Blocks);
        Assert.Equal(kind, block.Kind);
        Assert.Equal(line, CardText.From(card));
    }

    [Fact]
    public void A_run_of_dashes_is_one_list()
    {
        var card = CardText.Read("- one\n- two\n- three");

        var block = Assert.Single(card.Blocks);
        Assert.Equal(CardBlock.List, block.Kind);
        Assert.Equal(["one", "two", "three"], block.Items);
    }

    [Fact]
    public void A_numbered_run_is_an_index()
    {
        var card = CardText.Read("1. Wallet = KXJB7-MN2P4/sdpkt\n2. Chat = KXJB7-MN2P4/txtme");

        var block = Assert.Single(card.Blocks);
        Assert.Equal(CardBlock.Index, block.Kind);
        Assert.Equal(2, block.Items!.Count);
    }

    [Fact]
    public void A_blank_line_ends_a_list_rather_than_joining_the_next_one()
    {
        var card = CardText.Read("- one\n- two\n\n- three");

        Assert.Equal(2, card.Blocks.Count);
        Assert.All(card.Blocks, b => Assert.Equal(CardBlock.List, b.Kind));
    }

    [Fact]
    public void A_link_keeps_where_it_goes()
    {
        var card = CardText.Read("=> [My wallet](aether://KXJB7-MN2P4/sdpkt)");

        var block = Assert.Single(card.Blocks);
        Assert.Equal(CardBlock.Link, block.Kind);
        Assert.Equal("My wallet", block.Value);
        Assert.Equal("aether://KXJB7-MN2P4/sdpkt", block.Target);
    }

    [Fact]
    public void A_picture_keeps_the_hash_it_is_addressed_by()
    {
        var hash = new string('a', 64);
        var card = CardText.Read($"![On the bench]({hash})");

        var block = Assert.Single(card.Blocks);
        Assert.Equal(CardBlock.Image, block.Kind);
        Assert.Equal(hash, block.ContentHash);
        Assert.Equal("On the bench", block.Value);
    }

    [Fact]
    public void The_look_is_a_line_like_any_other()
    {
        var card = CardText.Read("%theme night");

        var block = Assert.Single(card.Blocks);
        Assert.Equal(CardBlock.Theme, block.Kind);
        Assert.Equal("night", block.Value);
    }

    [Fact]
    public void How_a_block_is_set_rides_at_the_end_of_its_line()
    {
        var card = CardText.Read("# Tabang Bhengu ::centre");

        var block = Assert.Single(card.Blocks);
        Assert.Equal(CardBlock.Title, block.Kind);
        Assert.Equal("Tabang Bhengu", block.Value);
        Assert.True(block.IsCentred);
        Assert.Equal("# Tabang Bhengu ::centre", CardText.From(card));
    }

    // ── What must not be lost ───────────────────────────────────────────────────

    [Fact]
    public void The_stylesheet_is_not_in_the_prose_but_is_not_thrown_away_either()
    {
        var was = CardText.Read("# A page\n\nSomething.");
        OwnCard.SetCss(was, "h1 { letter-spacing: -0.02em }");

        var written = CardText.From(was);
        Assert.DoesNotContain("letter-spacing", written);

        var back = CardText.Read(written, was);
        Assert.Equal("h1 { letter-spacing: -0.02em }",
            back.Blocks.Single(b => b.Kind == CardBlock.Css).Value);
    }

    [Fact]
    public void The_title_of_the_card_follows_the_title_in_the_document()
    {
        var card = CardText.Read("# The Geek Network\n\nSoftware built here.");

        Assert.Equal("The Geek Network", card.Title);
    }

    [Fact]
    public void An_empty_document_is_an_empty_card_rather_than_a_broken_one()
    {
        var card = CardText.Read("   \n\n  ");

        Assert.Empty(card.Blocks);
        Assert.Equal("", card.Title);
    }

    [Fact]
    public void Writing_a_card_that_has_nothing_in_it_says_nothing()
    {
        Assert.Equal("", CardText.From(new CardDocument()));
        Assert.Equal("", CardText.From(null));
    }
}
