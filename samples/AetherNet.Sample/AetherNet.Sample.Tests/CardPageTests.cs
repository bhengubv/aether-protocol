// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Somebody's own card, drawn for a stranger.
///
/// <para>
/// This page is the single moment where the whole network makes a first impression: a person who has
/// nothing installed, standing next to a friend, deciding whether to trust what is being offered. Two
/// properties carry all the weight, and both fail silently.
/// </para>
///
/// <para>
/// <b>It must reach for nothing.</b> The reader has joined a phone-to-phone network with no way out to
/// the internet, so an external font or stylesheet does not degrade — it never arrives, and the page
/// they are judging shows up half-drawn.
/// </para>
///
/// <para>
/// <b>And it must be inert.</b> Every string was typed by one person and is rendered to another. A card
/// that can run something is a way to reach inside a stranger's browser at the exact moment they are
/// least equipped to notice.
/// </para>
/// </summary>
public class CardPageTests
{
    private const string Download = "/tmb/abc123/aether.apk";

    private static CardDocument Card(params CardBlock[] blocks) =>
        new() { Title = "Thabang", Blocks = [.. blocks] };

    private static string Render(CardDocument? card, string? who = "Thabang") =>
        CardPage.Render(card, who, 51 * 1024 * 1024, Download);

    // ── It reaches for nothing ───────────────────────────────────────────────

    /// <summary>
    /// <b>Not one external reference.</b> No font host, no stylesheet, no image server, no remote script.
    /// </summary>
    /// <remarks>
    /// Checked by absence of the schemes rather than by inspection, because a single forgotten link tag
    /// is invisible on a machine that has internet and fatal on the phone that does not.
    /// </remarks>
    [Fact]
    public void The_page_reaches_for_nothing_outside_this_phone()
    {
        var html = Render(Card(
            CardBlock.Of(CardBlock.Heading, "About"),
            CardBlock.Of(CardBlock.Text, "I fix phones in Soweto.")));

        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//fonts.", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);

        // The page carries a script — the masthead painter — but never fetches one.
        Assert.DoesNotContain("<script src", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And it is the browser that enforces that, not us.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This page used to carry no script at all, which made "reaches for nothing" true by construction.
    /// It carries the masthead painter now, because the look is the argument this network makes before
    /// anybody reads a word — so the guarantee is restated as something the browser enforces rather
    /// than something the renderer promises.
    /// </para>
    /// <para>
    /// <c>default-src 'none'</c> is the whole of it: whatever ends up written into this page, it cannot
    /// open a connection, fetch an image from a host, load a remote font or reach anywhere at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_page_tells_the_browser_it_may_reach_nowhere()
    {
        var html = Render(Card());

        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", html, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A card with no look chosen asks for no typeface at all.
    /// </summary>
    /// <remarks>
    /// The default look is built from faces every handset already has, so a first card draws instantly
    /// and correctly before anybody has chosen anything or carried a single byte of font.
    /// </remarks>
    [Fact]
    public void A_card_with_no_look_chosen_needs_no_typeface()
    {
        var html = Render(Card());

        Assert.Contains("system-ui", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@font-face", html, StringComparison.Ordinal);
    }

    /// <summary>The styles travel with the page.</summary>
    [Fact]
    public void The_styles_are_inline()
    {
        Assert.Contains("<style>", Render(Card()), StringComparison.Ordinal);
    }

    // ── It is inert ──────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A card cannot run anything in a stranger's browser.</b>
    /// </summary>
    [Fact]
    public void A_card_cannot_carry_a_script()
    {
        var html = Render(Card(
            CardBlock.Of(CardBlock.Text, "<script>alert(1)</script>"),
            CardBlock.Of(CardBlock.Heading, "<img src=x onerror=alert(1)>")));

        // The payload survives as TEXT and never as markup. Asserting the word "onerror" is absent
        // would be wrong — it appears, harmlessly, inside escaped text. What must be absent is the
        // form a browser would act on.
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror", html, StringComparison.Ordinal);

        // Three scripts, all ours: the background this card named, the masthead painter, and the
        // link bridge. A fourth would mean something an author wrote had become executable, which is
        // the failure this exists to catch.
        Assert.Equal(3, Scripts(html));
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one script we do ship is handed nothing a person typed.
    /// </summary>
    /// <remarks>
    /// The masthead painter reads two values off the canvas: a colour that has already been through
    /// <see cref="CardBlock.IsUsableAccent"/>, and a seed the renderer computes as a number. Neither
    /// can carry a payload, so the escaping of a card's text never has to be right twice.
    /// </remarks>
    [Fact]
    public void Nothing_an_author_wrote_reaches_the_script()
    {
        const string payload = "PAYLOAD-ZZQ";

        var html = Render(
            Card(CardBlock.Of(CardBlock.Text, payload)),
            who: payload);

        var at = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        Assert.True(at >= 0, "the masthead painter is missing");

        Assert.DoesNotContain(payload, html[at..], StringComparison.Ordinal);
    }

    /// <summary>
    /// The page without its scripts.
    /// </summary>
    /// <remarks>
    /// The scripts we ship talk about the attributes they look for, so a check for markup that
    /// searched the whole document would find the code that reads it rather than an element that has
    /// it. What matters is what was written into the page, not what the renderer knows how to read.
    /// </remarks>
    private static string Markup(string html)
    {
        var at = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html : html[..at];
    }

    /// <summary>How many scripts the page carries.</summary>
    private static int Scripts(string html)
    {
        var count = 0;

        for (var at = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = html.IndexOf("<script", at + 1, StringComparison.OrdinalIgnoreCase))
            count++;

        return count;
    }

    /// <summary>A name cannot break out of the title or the heading.</summary>
    [Fact]
    public void A_name_cannot_break_out_of_its_element()
    {
        var html = Render(Card(), who: "</h1><script>alert(1)</script>");

        Assert.Equal(3, Scripts(html));
        Assert.Contains("&lt;/h1&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>Nor out of an attribute, where a quote would end the value.</summary>
    [Fact]
    public void Nothing_escapes_an_attribute()
    {
        var html = CardPage.Render(Card(), "Thabang", 1024,
            "/tmb/x\" onclick=\"alert(1)");

        // The quote is escaped, so the attribute never ends and onclick stays inside the value where
        // it means nothing. Checking for the word alone would fail on a page that is perfectly safe.
        Assert.DoesNotContain("\" onclick=\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&quot; onclick", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Control characters are dropped, not escaped.
    /// </summary>
    /// <remarks>
    /// Nothing legitimate on a card contains them, and they are how something hides from the eye of a
    /// person reading the card in a browser's view-source.
    /// </remarks>
    [Fact]
    public void Invisible_characters_are_dropped()
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Text, "safe text")));

        Assert.Contains("safetext", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", html, StringComparison.Ordinal);
    }

    // ── The person's own choices ─────────────────────────────────────────────

    /// <summary>A card is drawn in the look its author picked.</summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("terminal")]
    [InlineData("editorial")]
    [InlineData("studio")]
    [InlineData("night")]
    public void A_card_is_drawn_in_the_look_its_author_picked(string key)
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Theme, key)));

        Assert.Contains($"--paper:{CardLook.Of(key).Paper}", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every look we ship actually draws in its own colours.
    /// </summary>
    /// <remarks>
    /// A look carries its whole design — ground, ink, type, weight, leading, measure — so a look whose
    /// tokens never reach the page is a look that silently comes out as the one before it. Adding a
    /// look is adding a record, and this is what stops a new record being decorative.
    /// </remarks>
    [Fact]
    public void Every_look_is_drawn_in_its_own_colours()
    {
        foreach (var look in CardLook.All)
        {
            var html = Render(Card(CardBlock.Of(CardBlock.Theme, look.Key)));

            Assert.Contains($"--paper:{look.Paper}", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"--ink:{look.Ink}", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"--accent:{look.Accent}", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// And in its own typography — which is the half most likely to be left at a default.
    /// </summary>
    /// <remarks>
    /// Weight, size, leading and measure are what separate a page that reads as set from one that
    /// reads as filled in. Editorial at weight 400 and leading 1.5 is the same words and a different
    /// object, and nothing would have failed to tell us.
    /// </remarks>
    [Fact]
    public void Every_look_is_drawn_in_its_own_typography()
    {
        foreach (var look in CardLook.All)
        {
            var html = Render(Card(CardBlock.Of(CardBlock.Theme, look.Key)));

            Assert.Contains($"--weight:{look.BodyWeight}", html, StringComparison.Ordinal);
            Assert.Contains("--leading:", html, StringComparison.Ordinal);
            Assert.Contains("--measure:", html, StringComparison.Ordinal);
            Assert.Contains(look.Body.Split(',')[0], html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The reader has three states, not two: light, dark, and the default that stamps nothing.
    /// </summary>
    /// <remarks>
    /// A colour defined only inside a media query never applies in the un-stamped state, which is
    /// where most readers are — and the page then renders one theme's text on the other's ground.
    /// </remarks>
    [Fact]
    public void A_look_answers_for_a_light_ground_and_a_dark_one()
    {
        // A look that follows the reader. Editorial deliberately does not — see the material test.
        var html = Render(Card(CardBlock.Of(CardBlock.Theme, "plain")));
        var look = CardLook.Of("plain");

        Assert.Contains($"--paper:{look.Paper}", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"--paper:{look.PaperDark}", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefers-color-scheme:dark", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A look this build has never heard of falls back rather than failing.
    /// </summary>
    /// <remarks>
    /// The compatibility rule the whole model rests on: an unknown value is a newer author, not a
    /// broken card. A reader on an older build sees the page in the default look and reads every word
    /// of it.
    /// </remarks>
    [Fact]
    public void An_unknown_look_falls_back()
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Theme, "neon-brutalist-2029")));

        Assert.Contains($"--paper:{CardLook.Of(CardLook.DefaultKey).Paper}", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A plain hex accent is honoured.</summary>
    [Fact]
    public void A_plain_accent_is_honoured()
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Theme, "#2196F3")));

        Assert.Contains("--accent:#2196F3", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Anything richer than a colour is refused.</b>
    /// </summary>
    /// <remarks>
    /// The accent is written into a style, so a value carrying a second declaration is a card
    /// restyling the page around itself — including the parts that say who is offering what.
    /// </remarks>
    [Theory]
    [InlineData("red;position:fixed;inset:0")]
    [InlineData("url(http://evil/x)")]
    [InlineData("#fff;background:url(x)")]
    [InlineData("linear-gradient(red,blue)")]
    public void An_accent_that_is_not_a_colour_is_refused(string sneaky)
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Theme, sneaky)));

        Assert.Null(CardPage.AccentOf(Card(CardBlock.Of(CardBlock.Theme, sneaky))));
        Assert.DoesNotContain("position:fixed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", html, StringComparison.Ordinal);
    }

    // ── The blocks ───────────────────────────────────────────────────────────

    /// <summary>The author's words appear.</summary>
    [Fact]
    public void What_they_wrote_is_what_is_shown()
    {
        var html = Render(Card(
            CardBlock.Of(CardBlock.Heading, "What I do"),
            CardBlock.Of(CardBlock.Text, "I fix phones in Soweto."),
            new CardBlock { Kind = CardBlock.List, Items = ["Screens", "Batteries"] },
            CardBlock.Of(CardBlock.KeyValue, "Open=Mon to Sat")));

        Assert.Contains("What I do", html, StringComparison.Ordinal);
        Assert.Contains("I fix phones in Soweto.", html, StringComparison.Ordinal);
        Assert.Contains("Screens", html, StringComparison.Ordinal);
        Assert.Contains("Batteries", html, StringComparison.Ordinal);
        Assert.Contains("Open", html, StringComparison.Ordinal);
        Assert.Contains("Mon to Sat", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A block this renderer has never heard of is skipped, not fatal.
    /// </summary>
    /// <remarks>
    /// The compatibility rule the card model was built on: somebody on a newer version writes a block
    /// an older reader cannot draw, and the older reader still shows them a card.
    /// </remarks>
    [Fact]
    public void An_unknown_block_is_skipped_and_the_rest_still_draws()
    {
        var html = Render(Card(
            CardBlock.Of("hologram", "from the future"),
            CardBlock.Of(CardBlock.Text, "still here")));

        Assert.DoesNotContain("from the future", html, StringComparison.Ordinal);
        Assert.Contains("still here", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// On the page a stranger is handed, a link is shown as text and never as somewhere to go.
    /// </summary>
    /// <remarks>
    /// They have no Aether yet. A card link lives inside the mesh, so making it clickable is an
    /// invitation to a place their phone cannot follow — and the address itself never appears, since
    /// it would mean nothing to them and everything to somebody reading over their shoulder.
    /// </remarks>
    [Fact]
    public void A_link_is_not_clickable_for_somebody_who_cannot_follow_it()
    {
        var html = Render(Card(new CardBlock
        {
            Kind = CardBlock.Link,
            Value = "My shop",
            Target = "aether://KXJB7-MN2P4/shop",
        }));

        Assert.Contains("My shop", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aether://KXJB7", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-aether-to", Markup(html), StringComparison.Ordinal);
    }

    /// <summary>
    /// And on the mesh, where the reader can follow it, the same link works.
    /// </summary>
    /// <remarks>
    /// It still never becomes an address the page can act on: the target goes into a data attribute,
    /// the page asks its host to navigate, and the host checks it again. A card that could hand a
    /// browser an address of its own choosing is what publishing cards as JSON exists to prevent.
    /// </remarks>
    [Fact]
    public void A_link_works_for_a_reader_who_is_already_on_the_mesh()
    {
        var html = CardPage.Render(
            Card(new CardBlock
            {
                Kind = CardBlock.Link,
                Value = "My shop",
                Target = "aether://KXJB7-MN2P4/shop",
            }),
            "Thabang", 0, downloadPath: null);

        Assert.Contains("data-aether-to=\"aether://KXJB7-MN2P4/shop\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"aether://", html, StringComparison.Ordinal);
    }

    /// <summary>An http target is never followable, on either page.</summary>
    [Fact]
    public void A_link_that_points_off_the_mesh_goes_nowhere()
    {
        var html = CardPage.Render(
            Card(new CardBlock
            {
                Kind = CardBlock.Link,
                Value = "My shop",
                Target = "https://example.invalid/shop",
            }),
            "Thabang", 0, downloadPath: null);

        Assert.Contains("My shop", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-aether-to", Markup(html), StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// An image is drawn only when its bytes can actually be reached.
    /// </summary>
    /// <remarks>
    /// The renderer never invents an address for a hash. Without somewhere to serve it from, the block
    /// is left out — a broken image on the trust page is worse than no image.
    /// </remarks>
    [Fact]
    public void An_image_with_nowhere_to_come_from_is_left_out()
    {
        var block = new CardBlock { Kind = CardBlock.Image, ContentHash = "abc123", Value = "Me" };

        var without = CardPage.Render(Card(block), "Thabang", 1024, Download);
        var with = CardPage.Render(Card(block), "Thabang", 1024, Download,
            hash => $"/asset/{hash}");

        Assert.DoesNotContain("<img", without, StringComparison.Ordinal);
        Assert.Contains("/asset/abc123", with, StringComparison.Ordinal);
    }

    /// <summary>A hash that is really an address is refused.</summary>
    [Fact]
    public void An_image_hash_that_is_an_address_is_refused()
    {
        var block = new CardBlock { Kind = CardBlock.Image, ContentHash = "http://evil/x.png" };

        var html = CardPage.Render(Card(block), "Thabang", 1024, Download, h => h);

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", html, StringComparison.Ordinal);
    }

    // ── The offer itself ─────────────────────────────────────────────────────

    /// <summary>However personal the card, the offer is still on it.</summary>
    [Fact]
    public void The_offer_survives_whatever_the_author_did()
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Text, "hello")));

        Assert.Contains(Download, html, StringComparison.Ordinal);
        Assert.Contains("Get Aether", html, StringComparison.Ordinal);
        Assert.Contains("51 MB", html, StringComparison.Ordinal);
    }

    /// <summary>An empty card is still a page, not an error.</summary>
    [Fact]
    public void Somebody_who_wrote_nothing_still_has_a_card()
    {
        var html = CardPage.Render(null, "Thabang", 1024, Download);

        Assert.Contains("Thabang", html, StringComparison.Ordinal);
        Assert.Contains("Get Aether", html, StringComparison.Ordinal);
    }

    /// <summary>And somebody with no name at all is described rather than left blank.</summary>
    [Fact]
    public void Somebody_with_no_name_is_still_described()
    {
        var html = CardPage.Render(null, null, 1024, Download);

        Assert.Contains("Someone next to you", html, StringComparison.Ordinal);
    }

    /// <summary>The size is readable rather than a byte count.</summary>
    [Theory]
    [InlineData(0, "—")]
    [InlineData(4096, "4 KB")]
    [InlineData(51 * 1024 * 1024, "51 MB")]
    public void The_size_is_something_a_person_reads(long bytes, string expected)
    {
        Assert.Contains(expected, CardPage.Render(null, "T", bytes, Download), StringComparison.Ordinal);
    }

    // ── Looks ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A look brings its own typefaces, so two cards do not look the same with different colours.
    /// </summary>
    /// <remarks>
    /// The whole reason the unit is a look rather than a palette: what separates a page somebody paid
    /// a designer for from one they did not is mostly typography, and nobody without training reaches
    /// that from a colour picker.
    /// </remarks>
    [Fact]
    public void A_look_brings_its_own_type()
    {
        var editorial = Render(Card(CardBlock.Of(CardBlock.Theme, "editorial")));
        var terminal = Render(Card(CardBlock.Of(CardBlock.Theme, "terminal")));

        Assert.Contains("Instrument Serif", editorial, StringComparison.Ordinal);
        Assert.Contains("ui-monospace", terminal, StringComparison.Ordinal);
        Assert.NotEqual(editorial, terminal);
    }

    /// <summary>Every look we offer draws differently from the default.</summary>
    /// <remarks>
    /// A look in the list that renders identically to another is a choice a person makes and cannot
    /// see, which is worse than not offering it.
    /// </remarks>
    [Fact]
    public void Every_look_is_visibly_different()
    {
        var seen = CardLook.All
            .Select(l => Render(Card(CardBlock.Of(CardBlock.Theme, l.Key))))
            .ToArray();

        Assert.Equal(seen.Length, seen.Distinct().Count());
    }

    /// <summary>Each look declares a fallback the handset already has.</summary>
    /// <remarks>
    /// A lone family name is a look that collapses to the browser default when its face cannot be
    /// carried. Every stack names something real after it.
    /// </remarks>
    [Fact]
    public void Every_look_falls_back_to_something_the_phone_has()
    {
        foreach (var look in CardLook.All)
        {
            Assert.Contains(',', look.Display);
            Assert.Contains(',', look.Body);
            Assert.True(
                look.Body.Contains("serif", StringComparison.OrdinalIgnoreCase) ||
                look.Body.Contains("monospace", StringComparison.OrdinalIgnoreCase) ||
                look.Body.Contains("system-ui", StringComparison.OrdinalIgnoreCase),
                $"{look.Key} has no generic fallback");
        }
    }

    /// <summary>And the fallback is the default, not the first thing that happens to match.</summary>
    [Fact]
    public void An_unknown_look_resolves_to_the_default()
    {
        Assert.Equal(CardLook.DefaultKey, CardLook.Of("holographic").Key);
    }

    /// <summary>
    /// <b>A typeface is carried inside the page, or not at all.</b>
    /// </summary>
    /// <remarks>
    /// The reader has no internet. A linked font does not degrade — it never arrives — so it is
    /// embedded as bytes or the look falls through its own stack.
    /// </remarks>
    [Fact]
    public void A_typeface_travels_with_the_page()
    {
        byte[] face = [0x77, 0x4F, 0x46, 0x32];

        var carried = CardPage.Render(Card(CardBlock.Of(CardBlock.Theme, "editorial")),
            "Thabang", 1024, Download, null, _ => face);

        Assert.Contains("@font-face", carried, StringComparison.Ordinal);
        Assert.Contains("data:font/woff2;base64,", carried, StringComparison.Ordinal);

        // The scheme, not the word: the page declares its policy with an http-equiv meta tag, which
        // is not a reference to anywhere.
        Assert.DoesNotContain("http://", carried, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", carried, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And a face nobody can supply is simply left out.</summary>
    [Fact]
    public void A_typeface_that_cannot_be_carried_is_left_out()
    {
        var html = CardPage.Render(Card(CardBlock.Of(CardBlock.Theme, "editorial")),
            "Thabang", 1024, Download, null, _ => null);

        Assert.DoesNotContain("@font-face", html, StringComparison.Ordinal);
        Assert.Contains("Georgia", html, StringComparison.Ordinal);
    }
}
