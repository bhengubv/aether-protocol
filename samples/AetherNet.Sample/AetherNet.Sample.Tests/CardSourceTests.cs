// SPDX-License-Identifier: MIT

using System.Globalization;
using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Showing somebody how a page is made.
///
/// <para>
/// The answer to "what technical skill is this teaching?" — which for a long time was <i>none</i>. A
/// formatting toolbar teaches where the B is. What transfers is the model underneath: a page is a
/// list of typed pieces, a look is a handful of values, a background is a function, and the whole
/// thing is a small file you own.
/// </para>
///
/// <para>
/// So these tests are about one property, from four angles: what is shown must be the real thing,
/// not a description of it. A summary that paraphrases the page teaches nothing, because the moment
/// somebody changes what they are looking at and the page does not change, they stop believing it.
/// </para>
/// </summary>
public class CardSourceTests
{
    private static CardDocument Card() => new()
    {
        Title = "Kagiso Plumbing",
        Blocks =
        [
            CardBlock.Of(CardBlock.Theme, "editorial"),
            CardBlock.Of(CardBlock.Theme, "tide"),
            CardBlock.Of(CardBlock.Title, "Kagiso Plumbing"),
            CardBlock.Of(CardBlock.Text, "We come out on a Sunday."),
            new CardBlock { Kind = CardBlock.Image, ContentHash = "abc123", Value = "The van" },
        ],
    };

    // ── A page is a list of pieces ────────────────────────────────────────────

    [Fact]
    public void Every_piece_is_listed_in_the_order_a_reader_meets_them()
    {
        var read = CardSource.Of(Card());

        Assert.Equal(
            ["theme", "theme", "title", "text", "image"],
            read.Pieces.Select(p => p.Kind));
    }

    /// <summary>
    /// Including the two that carry the design.
    /// </summary>
    /// <remarks>
    /// Tempting to tidy them away as machinery. They are the opposite: somebody reading this to learn
    /// how a page works should see that the look and the background are stored <i>in</i> the document
    /// like every other piece, not applied to it from somewhere they cannot reach.
    /// </remarks>
    [Fact]
    public void The_design_is_shown_to_be_part_of_the_document()
    {
        var read = CardSource.Of(Card());
        var themes = read.Pieces.Where(p => p.Kind == CardBlock.Theme).Select(p => p.Said).ToArray();

        Assert.Contains("editorial", themes);
        Assert.Contains("tide", themes);
    }

    // ── A look is a handful of values ─────────────────────────────────────────

    /// <summary>
    /// The look is shown as what it is, not as what it is called.
    /// </summary>
    /// <remarks>
    /// "Editorial" is a name and a name teaches nothing. Eleven colours and numbers is a design
    /// system, and somebody who sees that the whole page comes out of eleven values has learned what
    /// a design token is without the phrase being used on them.
    /// </remarks>
    [Fact]
    public void The_look_is_shown_as_the_values_it_is()
    {
        var read = CardSource.Of(Card());
        var look = CardLook.Of("editorial");

        Assert.Equal("Editorial", read.LookName);
        Assert.Contains(read.Tokens, t => t.Value == look.Paper);
        Assert.Contains(read.Tokens, t => t.Value == look.Ink);
        Assert.Contains(read.Tokens, t => t.Value == look.Accent);
        Assert.Contains(read.Tokens, t => t.Value == $"{look.BodySize}px");
    }

    /// <summary>And each value says what it does, because a hex code on its own is a riddle.</summary>
    [Fact]
    public void Each_value_says_what_it_does()
    {
        foreach (var token in CardSource.Of(Card()).Tokens)
        {
            Assert.False(string.IsNullOrWhiteSpace(token.Name), "a value with no name");
            Assert.False(string.IsNullOrWhiteSpace(token.Says), $"'{token.Name}' does not say what it does");
        }
    }

    /// <summary>
    /// The values are written the way a stylesheet writes them, wherever the phone is.
    /// </summary>
    /// <remarks>
    /// Caught on the bench, on a handset set to a locale that puts a comma in a decimal: the panel
    /// said the leading was "1,74" while the page it claims to be showing said 1.74. These are values
    /// from a stylesheet, not numbers in a sentence — and the whole promise being made to the reader
    /// is that what they are looking at is the real thing, so somebody who copies it and gets invalid
    /// CSS has been told something untrue by the one screen that exists to be trusted.
    /// </remarks>
    [Fact]
    public void The_values_are_written_the_way_a_stylesheet_writes_them()
    {
        var was = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("af-ZA");

            // The typeface stacks are commas doing their job; the numbers are the ones that matter.
            foreach (var token in CardSource.Of(Card()).Tokens)
                if (token.Value.Any(char.IsDigit))
                    Assert.DoesNotContain(",", token.Value, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    // ── A background is a function ────────────────────────────────────────────

    /// <summary>
    /// The background is shown as its source, not as a picture of itself.
    /// </summary>
    /// <remarks>
    /// This is the moment worth building the whole panel for: it is not an image, it is one function
    /// run once per pixel, and it fits on a phone screen. Somebody who changes a number in it and
    /// watches the page move has written a fragment shader.
    /// </remarks>
    [Fact]
    public void The_background_is_shown_as_the_function_that_draws_it()
    {
        var read = CardSource.Of(Card());

        Assert.Equal("Tide", read.BackName);
        Assert.Contains("float field(vec2 p)", read.Field, StringComparison.Ordinal);
        Assert.Equal(CardShader.Of("tide").Field, read.Field);
    }

    /// <summary>
    /// A page that never chose a background still has one, and it is shown.
    /// </summary>
    /// <remarks>
    /// Every card gets a background whether or not its author picked one, so "no background" is not a
    /// state a page can be in. What matters is that this shows the function that is actually running
    /// rather than the one the document happens to name — somebody who reads the source, changes a
    /// number and sees nothing move has been told something untrue.
    /// </remarks>
    [Fact]
    public void A_page_that_never_chose_a_background_is_shown_the_one_it_has()
    {
        var plain = new CardDocument { Blocks = [CardBlock.Of(CardBlock.Text, "Just words.")] };
        var read = CardSource.Of(plain);

        Assert.Equal(CardShader.FromCard(plain).Key, read.Back);
        Assert.Equal(CardShader.FromCard(plain).Field, read.Field);
        Assert.Contains("float field(vec2 p)", read.Field, StringComparison.Ordinal);
    }

    // ── And you own the whole thing ───────────────────────────────────────────

    /// <summary>
    /// The document shown is the document that travels.
    /// </summary>
    /// <remarks>
    /// Byte for byte, because the claim being made to the reader is "this is the file, it is on your
    /// phone, nobody can take it back". A prettied-up rendering of the file would make that claim
    /// false in the one place somebody is being invited to check it.
    /// </remarks>
    [Fact]
    public void The_document_shown_is_the_document_that_travels()
    {
        var card = Card();

        Assert.Equal(card.ToJson(), CardSource.Of(card).Json);
    }

    /// <summary>
    /// And it says what the page weighs, and where the weight actually is.
    /// </summary>
    /// <remarks>
    /// A page of writing is a few kilobytes; the pictures are the file. Somebody learning what a page
    /// costs should be told the honest split rather than one number that hides it.
    /// </remarks>
    [Fact]
    public void It_says_what_the_page_weighs_and_where_the_weight_is()
    {
        var read = CardSource.Of(Card());

        Assert.True(read.Bytes > 0 && read.Bytes < 4096, $"{read.Bytes} bytes");
        Assert.Equal(1, read.Pictures);
    }

    /// <summary>
    /// A block carries what it stores, and not the answers to questions about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found by this panel, on the phone, within a minute of it existing — which is the argument for
    /// the panel. Every block was serialising <c>IsCentred</c>, <c>IsWide</c>, <c>IsWash</c> and
    /// <c>IsSmall</c>: four booleans derived from two fields that were already there, written once per
    /// block, carried over a radio, and stored on every phone that held the card. On the page being
    /// used as the standard it was 1,656 bytes of a 3,930-byte document — forty-two per cent of it.
    /// </para>
    /// <para>
    /// The size is the smaller half. This format exists to be read by somebody learning how it works,
    /// and it was showing them four fields that look settable and are not.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("IsCentred")]
    [InlineData("IsWide")]
    [InlineData("IsWash")]
    [InlineData("IsSmall")]
    public void A_block_does_not_carry_answers_to_questions_about_itself(string asked)
    {
        var card = new CardDocument
        {
            Blocks = [new CardBlock { Kind = CardBlock.Image, ContentHash = "abc123", As = "wide", Align = "centre" }],
        };

        Assert.DoesNotContain(asked, card.ToJson(), StringComparison.Ordinal);
    }

    /// <summary>And what it does store still survives the trip.</summary>
    [Fact]
    public void What_a_block_does_store_survives_the_trip()
    {
        var card = new CardDocument
        {
            Blocks = [new CardBlock { Kind = CardBlock.Image, ContentHash = "abc123", As = "wide", Align = "centre" }],
        };

        var back = System.Text.Json.JsonSerializer.Deserialize<CardDocument>(card.ToJson())!;
        var block = Assert.Single(back.Blocks);

        Assert.True(block.IsWide);
        Assert.True(block.IsCentred);
    }

    [Fact]
    public void Reading_nothing_is_an_empty_page_rather_than_a_crash()
    {
        var read = CardSource.Of(null);

        Assert.Empty(read.Pieces);
        Assert.NotEmpty(read.Tokens);
    }
}
