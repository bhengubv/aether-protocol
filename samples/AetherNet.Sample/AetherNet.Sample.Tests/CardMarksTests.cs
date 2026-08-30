// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Emphasis and links inside a sentence.
///
/// <para>
/// A page whose prose cannot link is a leaflet. Every real page links from inside its sentences —
/// the page this whole standard is measured against does it eleven times — so a card has to, or "the
/// same page in both places" is not a true sentence.
/// </para>
///
/// <para>
/// It is also the one piece of a card where the author writes something the reader's browser might
/// act on, which makes it the piece worth testing hardest. The rule is that a written address is
/// never taken at its word, and most of what follows is that rule from a different angle each time.
/// </para>
/// </summary>
public class CardMarksTests
{
    private static string Draw(string words) => CardMarks.Draw(words);

    // ── What the marks are ────────────────────────────────────────────────────

    [Theory]
    [InlineData("a **loud** word", "a <strong>loud</strong> word")]
    [InlineData("a *quiet* word", "a <em>quiet</em> word")]
    [InlineData("a _lined_ word", "a <u>lined</u> word")]
    public void The_marks_draw_what_they_say(string written, string drawn) =>
        Assert.Equal(drawn, Draw(written));

    /// <summary>
    /// Two asterisks are bold, not italics that started early.
    /// </summary>
    /// <remarks>
    /// The double has to be looked for first. Reading left to right and taking the first star as
    /// emphasis leaves the second one sitting in the middle of the sentence, and nobody who typed
    /// <c>**loud**</c> meant that.
    /// </remarks>
    [Fact]
    public void Bold_is_not_italics_that_started_early()
    {
        Assert.Equal("<strong>loud</strong>", Draw("**loud**"));
        Assert.DoesNotContain("<em>", Draw("**loud**"), StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_nest()
    {
        Assert.Equal("<strong>a <em>very</em> loud word</strong>", Draw("**a *very* loud word**"));
    }

    /// <summary>
    /// An asterisk somebody used as an asterisk stays an asterisk.
    /// </summary>
    /// <remarks>
    /// And only that asterisk: the emphasis somebody put in deliberately still works in the same
    /// sentence. A mark has to open against a word and close against a word, which is the rule that
    /// separates arithmetic from italics — without it, everything between the two stars here is set
    /// in italics and the writer has no way to say otherwise.
    /// </remarks>
    [Fact]
    public void An_unmatched_mark_is_a_character()
    {
        Assert.Equal("2 * 3, and <em>this</em> counts", Draw("2 * 3, and *this* counts"));
    }

    [Fact]
    public void Nothing_between_a_pair_is_two_characters_somebody_typed()
    {
        Assert.Equal("**", Draw("**"));
        Assert.Equal("__", Draw("__"));
    }

    // ── Links, and what a written address is allowed to do ────────────────────

    /// <summary>
    /// A web address becomes an ordinary anchor. Nothing is fetched to draw it.
    /// </summary>
    [Fact]
    public void A_web_address_is_a_link_the_reader_may_follow()
    {
        var drawn = Draw("see [the shop](https://kagiso.example/shop) today");

        Assert.Contains("<a class=\"mk\" href=\"https://kagiso.example/shop\"", drawn, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", drawn, StringComparison.Ordinal);
        Assert.Contains(">the shop</a>", drawn, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mesh address is asked for, never followed.
    /// </summary>
    /// <remarks>
    /// The same rule as a link block, for the same reason: the card never gets an address of its own
    /// to act on. It posts a message, the host checks the address again, and the host decides. A card
    /// that could hand a browser an address of its choosing is what publishing cards as signed JSON
    /// instead of HTML exists to prevent.
    /// </remarks>
    [Fact]
    public void A_mesh_address_asks_the_host_rather_than_going_anywhere()
    {
        var drawn = Draw("my [other page](aether://Y6TK9-EW9KK/shop) has the prices");

        Assert.Contains("data-aether-to=\"aether://Y6TK9-EW9KK/shop\"", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("href", drawn, StringComparison.Ordinal);
    }

    /// <summary>
    /// Somebody with no Aether is shown the words instead of a link that goes nowhere.
    /// </summary>
    [Fact]
    public void A_stranger_is_shown_the_words()
    {
        var drawn = CardMarks.Draw("my [other page](aether://Y6TK9-EW9KK/shop) has the prices", offering: true);

        Assert.Equal("my other page has the prices", drawn);
    }

    /// <summary>
    /// Every other scheme is words with nothing to click.
    /// </summary>
    /// <remarks>
    /// This is the whole point of checking rather than trusting. A card is a document a stranger
    /// wrote, and <c>javascript:</c> in an href is the shortest path from "I opened somebody's page"
    /// to "somebody's page is running in my app". So the address is not neutered or escaped or
    /// rewritten — no element is produced at all, and the sentence still reads.
    /// </remarks>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>hi")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://kagiso.example")]
    [InlineData("https://user@evil.example")]
    [InlineData("https://nodot")]
    public void An_address_that_is_not_https_or_mesh_is_only_words(string target)
    {
        var drawn = CardMarks.Draw("see [the shop](" + target + ") today");

        Assert.Equal("see the shop today", drawn);
        Assert.DoesNotContain("<a", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", drawn, StringComparison.Ordinal);
    }

    /// <summary>
    /// A quote inside an address cannot end the attribute it sits in.
    /// </summary>
    /// <remarks>
    /// Marks are drawn on text that is already escaped, which is what makes this safe: by the time an
    /// address reaches the href its quotes are entities, and an entity inside an attribute is a
    /// character in a URL rather than the end of the attribute. Order is the whole defence, so it is
    /// worth a test that fails loudly if anybody ever swaps it.
    /// </remarks>
    [Fact]
    public void A_quote_in_an_address_cannot_escape_the_attribute()
    {
        var written = "[shop](https://k.example/\" onmouseover=\"alert(1))";
        var drawn = CardPage.Render(
            new CardDocument { Blocks = [CardBlock.Of(CardBlock.Text, written)] },
            "A page", 0, downloadPath: null);

        Assert.DoesNotContain("onmouseover=\"", drawn, StringComparison.Ordinal);
    }

    /// <summary>Words that look like a tag are words, whatever else is going on around them.</summary>
    [Fact]
    public void Written_markup_stays_written()
    {
        var drawn = CardPage.Render(
            new CardDocument
            {
                Blocks = [CardBlock.Of(CardBlock.Text, "**<script>alert(1)</script>**")],
            },
            "A page", 0, downloadPath: null);

        Assert.DoesNotContain("<script>alert", drawn, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", drawn, StringComparison.Ordinal);
    }

    // ── Where marks reach ─────────────────────────────────────────────────────

    /// <summary>Prose is prose wherever it is written.</summary>
    [Fact]
    public void A_quote_and_a_list_can_be_marked_too()
    {
        var card = new CardDocument
        {
            Blocks =
            [
                CardBlock.Of(CardBlock.Quote, "the *whole* point"),
                new CardBlock { Kind = CardBlock.List, Items = ["a **fixed** price"] },
            ],
        };

        var html = CardPage.Render(card, "A page", 0, downloadPath: null);

        Assert.Contains("<blockquote>the <em>whole</em> point</blockquote>", html, StringComparison.Ordinal);
        Assert.Contains("<li>a <strong>fixed</strong> price</li>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Return once is a new line; twice is a new paragraph.
    /// </summary>
    /// <remarks>
    /// Somebody writing prose presses Return and means one of two things, and both of them are drawn
    /// here rather than being answered with "make another block". The block is the piece of writing,
    /// not the sentence.
    /// </remarks>
    [Fact]
    public void A_blank_line_starts_a_paragraph()
    {
        var card = new CardDocument
        {
            Blocks = [CardBlock.Of(CardBlock.Text, "First thought.\n\nSecond thought.\nSame breath.")],
        };

        var html = CardPage.Render(card, "A page", 0, downloadPath: null);

        Assert.Contains("<p class=\"say\">First thought.</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"say\">Second thought.<br>Same breath.</p>", html, StringComparison.Ordinal);
    }
}
