// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The standard, taken as a test of the <b>editor</b> rather than of the model.
///
/// <para>
/// Meng To's sketchbook — the page everything here has been measured against — rebuilt using only
/// operations a person actually has: start from a template, add a block of a kind the Add row offers,
/// type into it, set its alignment, choose a look and a background. Nothing is hand-assembled.
/// </para>
///
/// <para>
/// That distinction is the whole point of this file. A document built in C# proves the format can
/// hold the content, which was never in doubt; it proves nothing about whether somebody sitting on a
/// bus with a handset can get there. So every block below arrives through <see cref="OwnCard.Add"/>,
/// and the test fails if the Add row stops offering it.
/// </para>
///
/// <para>
/// Two things on that page are deliberately out of reach and stay that way: the sketchbook you drag
/// to turn, and the glass you move across it. A card is opened on a stranger's phone, so it does not
/// run its author's code. Everything a reader <i>looks at</i> is in scope; everything they play with
/// is not.
/// </para>
/// </summary>
public class SketchbookStandardTests
{
    private static string Draw() =>
        CardPage.Render(
            Sketchbook.Built(), "Meng To", 0, downloadPath: null,
            assetPath: _ => "data:image/jpeg;base64,AAAA", fonts: PageAssets.Face);

    // ── Everything on that page, element by element ───────────────────────────

    /// <summary>
    /// The top of the page reads in the order its author wrote it: wordmark, navigation, then the
    /// line saying who this is.
    /// </summary>
    /// <remarks>
    /// Which is not the order a renderer would choose, and that is the point. This one used to lift
    /// the title to the top of every page and the label above it — so a page could not open with a
    /// picture, could not put its navigation over its name, and could not do any of the things a
    /// designed page does. Three of the author's decisions, all of them made for them.
    /// </remarks>
    [Fact]
    public void The_top_of_the_page_is_in_the_authors_order()
    {
        var html = Draw();

        // The heading in the page, not the one in the head — the browser tab carries the name too.
        var name = html.IndexOf("<h1", StringComparison.Ordinal);
        var nav = html.IndexOf("<nav", StringComparison.Ordinal);
        var brow = html.IndexOf("AI Educator", StringComparison.Ordinal);

        Assert.True(name > 0 && name < nav, "the name is not first");
        Assert.True(nav < brow, "the navigation is not above the role line");
        Assert.Contains("class=\"mid wordmark\"", html, StringComparison.Ordinal);
    }

    /// <summary>Journal / About / Contact, as a row rather than a stack of buttons.</summary>
    [Fact]
    public void The_pages_own_navigation_is_a_row()
    {
        var html = Draw();

        // A row, wherever the author put it on the page — the alignment is theirs to choose.
        Assert.Contains("<nav class=\"row", html, StringComparison.Ordinal);
        Assert.Contains("Journal", html, StringComparison.Ordinal);
        Assert.Contains("Contact", html, StringComparison.Ordinal);
    }

    /// <summary>The spread runs to the edges at its own shape, not cropped into a header band.</summary>
    [Fact]
    public void The_spread_is_shown_whole()
    {
        Assert.Contains("figure class=\"wide\"", Draw(), StringComparison.Ordinal);
    }

    /// <summary>Nine plates as a gallery — the thing a page of stacked images cannot be.</summary>
    [Fact]
    public void The_plates_are_a_gallery()
    {
        var html = Draw();

        Assert.Contains("class=\"gallery\"", html, StringComparison.Ordinal);
        Assert.True(html.Split("<figcaption>").Length - 1 >= 9, "not every plate is captioned");
    }

    [Fact]
    public void The_plate_index_is_numbered_by_the_renderer()
    {
        var html = Draw();

        Assert.Contains("class=\"plate-row\"", html, StringComparison.Ordinal);
        Assert.Contains("01", html, StringComparison.Ordinal);
        Assert.Contains("09", html, StringComparison.Ordinal);
        Assert.Contains("Botanic Gardens", html, StringComparison.Ordinal);
        Assert.Contains("Tanglin", html, StringComparison.Ordinal);
    }

    /// <summary>A mark between the parts, not a hairline.</summary>
    [Fact]
    public void The_breaks_carry_a_mark()
    {
        var html = Draw();

        Assert.Contains("class=\"brk\"", html, StringComparison.Ordinal);
        Assert.Contains(CardOrnament.Of("brush").Draw[..40], html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A phrase linked from inside a sentence, which is what the page actually does.
    /// </summary>
    /// <remarks>
    /// All three of the emphasised phrases on that page turn out to be links — the studio, the medium,
    /// the address. A card used to have no way to say that, so they were written as emphasis and the
    /// page was nearly right for a reason nobody would have found by looking at it.
    /// </remarks>
    [Fact]
    public void A_phrase_inside_a_sentence_can_be_a_link()
    {
        var html = Draw();

        Assert.Contains("href=\"https://designcode.io\"", html, StringComparison.Ordinal);
        Assert.Contains(">Design+Code</a>", html, StringComparison.Ordinal);
        Assert.Contains("<em>in ink and a little colour</em></a>", html, StringComparison.Ordinal);
        Assert.Contains(">hello@mengto.com</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_closing_line_is_centred()
    {
        Assert.Contains("class=\"say\" class=\"mid\"", Draw().Replace("\" class=\"mid\"", "\" class=\"mid\""),
            StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_set_in_the_typefaces_it_was_measured_from()
    {
        var html = Draw();

        Assert.Contains("Instrument Serif", html, StringComparison.Ordinal);
        Assert.Contains("Newsreader", html, StringComparison.Ordinal);
        Assert.Contains("--weight:300", html, StringComparison.Ordinal);
    }

    // ── And a person could actually have made it ──────────────────────────────

    /// <summary>
    /// Every block on the page is one the Add row offers.
    /// </summary>
    /// <remarks>
    /// The assertion that keeps this a test of the editor. <see cref="Write"/> already fails on a kind
    /// the editor will not add; this says the same thing about the finished document, so removing a
    /// kind from the Add row cannot quietly leave the page still building.
    /// </remarks>
    [Fact]
    public void Every_block_on_it_is_one_somebody_can_add()
    {
        foreach (var kind in Sketchbook.Built().Blocks.Select(b => b.Kind).Distinct().Where(k => k != CardBlock.Theme))
            Assert.Contains(kind, OwnCard.Writable);
    }

    /// <summary>And a page this size still fits inside what one card may hold.</summary>
    [Fact]
    public void It_fits_inside_what_a_card_may_hold()
    {
        var card = Sketchbook.Built();

        Assert.True(card.Blocks.Count <= OwnCard.MostBlocks,
            $"{card.Blocks.Count} blocks, and a card holds {OwnCard.MostBlocks}");

        Assert.True(card.Blocks.Count(b => b.Kind == CardBlock.Image) <= PagePhoto.MostPerPage,
            "more pictures than a page carries");
    }

    [Fact]
    public void The_whole_page_survives_being_published()
    {
        var sent = OwnCard.ForPublish(Sketchbook.Built());

        foreach (var kind in new[]
                 { CardBlock.Eyebrow, CardBlock.Link, CardBlock.Image, CardBlock.Heading,
                   CardBlock.Text, CardBlock.Index, CardBlock.Rule })
            Assert.Contains(sent.Blocks, b => b.Kind == kind);

        Assert.Equal(9, Assert.Single(sent.Blocks, b => b.Kind == CardBlock.Index).Items!.Count);
    }

    // ── Written out, so it can be looked at ───────────────────────────────────

    /// <summary>
    /// Render it beside the test run, for eyes rather than assertions.
    /// </summary>
    [Fact]
    public void The_page_is_written_out_to_be_looked_at()
    {
        var into = Environment.GetEnvironmentVariable("AETHER_FIXTURES");
        if (!string.IsNullOrWhiteSpace(into) && Directory.Exists(into))
            File.WriteAllText(Path.Combine(into, "sketchbook.html"), Draw());

        Assert.Contains("Meng To", Draw(), StringComparison.Ordinal);
    }
}
