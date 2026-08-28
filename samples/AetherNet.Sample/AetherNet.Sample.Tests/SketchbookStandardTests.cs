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
    /// <summary>
    /// The page, built the way a person builds one.
    /// </summary>
    /// <remarks>
    /// Blank template, then blocks added one at a time and filled in — the same calls the editor's
    /// buttons make. If a kind here ever leaves <see cref="OwnCard.Writable"/>, this stops compiling
    /// into a page and the test says so.
    /// </remarks>
    private static CardDocument Built()
    {
        var card = PageTemplate.Of("blank").Build(null);
        card.Blocks.RemoveAll(b => b.Kind == CardBlock.Text);

        card.Title = "Meng To";
        OwnCard.SetLook(card, "editorial");
        OwnCard.SetShader(card, "tide");

        Write(card, CardBlock.Eyebrow, "Designer / Creator / AI Educator / Founder @ Singapore", centred: true);

        Write(card, CardBlock.Link, "Journal", to: "aether://MENGTO/journal");
        Write(card, CardBlock.Link, "About", to: "aether://MENGTO/about");
        Write(card, CardBlock.Link, "Contact", to: "aether://MENGTO/contact");

        Write(card, CardBlock.Image, "The sketchbook, open at Marina Bay", hash: "spread01", wide: true);

        Write(card, CardBlock.Heading, "About");
        Write(card, CardBlock.Text,
            "Meng To is a designer, creator and AI educator based in Singapore, founder of " +
            "_Design+Code_, where for over a decade he has taught designers and developers to " +
            "build real apps.");
        Write(card, CardBlock.Text,
            "This sketchbook is the other half of that — the city looked at slowly, *in ink and a " +
            "little colour*: shophouse shutters, hawker tents, the bay at dusk.");

        Write(card, CardBlock.Rule, "brush");

        Write(card, CardBlock.Heading, "Plates");
        Lines(card, CardBlock.Index,
            "Marina Bay Sands = Bayfront",
            "Gardens by the Bay = Supertree Grove",
            "The Merlion = Merlion Park",
            "Buddha Tooth Relic Temple = Chinatown",
            "Joo Chiat Shophouses = Katong",
            "Lau Pa Sat = Raffles Quay",
            "Marina Bay Skyline = The Bay",
            "Singapore River = Boat Quay",
            "Botanic Gardens = Tanglin");

        // Nine plates, added the way somebody adds pictures — one after another. Consecutive pictures
        // become a gallery; nobody has to know that.
        foreach (var plate in new[]
                 { "Marina Bay Sands", "Gardens by the Bay", "The Merlion", "Buddha Tooth Relic Temple",
                   "Joo Chiat Shophouses", "Lau Pa Sat", "Marina Bay Skyline", "Singapore River",
                   "Botanic Gardens" })
            Write(card, CardBlock.Image, plate, hash: "plate" + plate.Length + plate[..2].ToLowerInvariant());

        Write(card, CardBlock.Rule, "scatter");
        Write(card, CardBlock.Text, "Singapore · Sketchbook · _hello@mengto.com_", centred: true);

        return OwnCard.Tidy(card);
    }

    /// <summary>Add a block of this kind and fill it in — exactly what the editor's buttons do.</summary>
    private static void Write(
        CardDocument card, string kind, string? value = null,
        string? to = null, string? hash = null, bool centred = false, bool wide = false)
    {
        Assert.True(OwnCard.Add(card, kind), $"the editor does not offer {kind}");

        var block = card.Blocks[^1];
        block.Value = value;
        block.Target = to;
        block.ContentHash = hash;
        if (centred) block.Align = "centre";
        if (wide) block.As = "wide";
    }

    private static void Lines(CardDocument card, string kind, params string[] lines)
    {
        Assert.True(OwnCard.Add(card, kind), $"the editor does not offer {kind}");
        card.Blocks[^1].Items = [.. lines];
    }

    private static string Draw() =>
        CardPage.Render(
            Built(), "Meng To", 0, downloadPath: null,
            assetPath: _ => "data:image/jpeg;base64,AAAA", fonts: PageAssets.Face);

    // ── Everything on that page, element by element ───────────────────────────

    [Fact]
    public void The_role_line_sits_centred_above_the_name()
    {
        var html = Draw();

        var brow = html.IndexOf("AI Educator", StringComparison.Ordinal);
        var title = html.IndexOf("<h1", StringComparison.Ordinal);

        Assert.True(brow > 0 && brow < title, "the role line is not above the name");
        Assert.Contains("class=\"eyebrow mid\"", html, StringComparison.Ordinal);
    }

    /// <summary>Journal / About / Contact, as a row rather than a stack of buttons.</summary>
    [Fact]
    public void The_pages_own_navigation_is_a_row()
    {
        var html = Draw();

        Assert.Contains("class=\"row\"", html, StringComparison.Ordinal);
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

    /// <summary>A phrase stressed inside a sentence, which is most of what a page's voice is.</summary>
    [Fact]
    public void A_phrase_inside_a_sentence_can_be_stressed()
    {
        var html = Draw();

        Assert.Contains("<u>Design+Code</u>", html, StringComparison.Ordinal);
        Assert.Contains("<em>in ink and a little colour</em>", html, StringComparison.Ordinal);
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
        foreach (var kind in Built().Blocks.Select(b => b.Kind).Distinct().Where(k => k != CardBlock.Theme))
            Assert.Contains(kind, OwnCard.Writable);
    }

    /// <summary>And a page this size still fits inside what one card may hold.</summary>
    [Fact]
    public void It_fits_inside_what_a_card_may_hold()
    {
        var card = Built();

        Assert.True(card.Blocks.Count <= OwnCard.MostBlocks,
            $"{card.Blocks.Count} blocks, and a card holds {OwnCard.MostBlocks}");

        Assert.True(card.Blocks.Count(b => b.Kind == CardBlock.Image) <= PagePhoto.MostPerPage,
            "more pictures than a page carries");
    }

    [Fact]
    public void The_whole_page_survives_being_published()
    {
        var sent = OwnCard.ForPublish(Built());

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
