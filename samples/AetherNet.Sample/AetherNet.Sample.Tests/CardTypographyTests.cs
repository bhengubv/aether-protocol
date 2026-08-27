// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The blocks and the typography that let a card hold its own against a real website.
///
/// <para>
/// A mesh page that reads as a form nobody will publish one, and nobody will hand one on. So the
/// vocabulary has to reach: a label above a title, a pulled quote, a break between passages, a
/// picture with something written under it, and — the one that does most of the work — a numbered
/// index, which is the shape a catalogue, a menu, a price list, a set of works and a schedule all
/// share.
/// </para>
///
/// <para>
/// None of this is decoration. Everything here fails silently: a look whose numbers never reach the
/// page comes out as the look before it, a caption that is dropped takes a photograph's meaning with
/// it, and an index that renders as a bulleted list is the difference between a page that was set and
/// a page that was filled in. Nothing would have told us.
/// </para>
/// </summary>
public class CardTypographyTests
{
    private static CardDocument Card(params CardBlock[] blocks) =>
        new() { Title = "Kagiso Plumbing", Blocks = [.. blocks] };

    private static string Render(CardDocument card) =>
        CardPage.Render(card, card.Title, 0, downloadPath: null);

    // ── The index ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A name, where it belongs, and a number nobody had to type.
    /// </summary>
    [Fact]
    public void An_index_is_numbered_for_its_author()
    {
        var html = Render(Card(new CardBlock
        {
            Kind = CardBlock.Index,
            Items = ["Geyser replacement = From R2400", "Blocked drain = From R650"],
        }));

        Assert.Contains("01", html, StringComparison.Ordinal);
        Assert.Contains("02", html, StringComparison.Ordinal);
        Assert.Contains("Geyser replacement", html, StringComparison.Ordinal);
        Assert.Contains("From R2400", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line with no place is still a line. A catalogue that silently dropped an unlabelled entry
    /// would be a catalogue missing an entry, which is worse than one that looks slightly uneven.
    /// </summary>
    [Fact]
    public void An_index_line_with_no_place_is_still_a_line()
    {
        var html = Render(Card(new CardBlock
        {
            Kind = CardBlock.Index,
            Items = ["Just the one thing"],
        }));

        Assert.Contains("Just the one thing", html, StringComparison.Ordinal);
        Assert.Contains("01", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_with_nothing_in_it_is_not_published()
    {
        var card = Card(new CardBlock { Kind = CardBlock.Index, Items = ["", "  "] });

        Assert.DoesNotContain(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.Index);
    }

    [Fact]
    public void An_index_keeps_only_the_lines_somebody_wrote()
    {
        var card = Card(new CardBlock { Kind = CardBlock.Index, Items = ["A = 1", "", "B = 2"] });

        var index = Assert.Single(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.Index);

        Assert.Equal(["A = 1", "B = 2"], index.Items);
    }

    // ── The eyebrow ───────────────────────────────────────────────────────────

    /// <summary>
    /// A label belongs above the title, wherever its author put it in the document.
    /// </summary>
    /// <remarks>
    /// It qualifies the title rather than sitting in the flow. Rendering it in document order would
    /// mean an author had to know to place it first — which is exactly the kind of thing a person
    /// writing on a handset should never have to know.
    /// </remarks>
    [Fact]
    public void A_label_is_drawn_above_the_title_wherever_it_was_written()
    {
        var html = Render(Card(
            CardBlock.Of(CardBlock.Text, "Some words that come first in the document."),
            CardBlock.Of(CardBlock.Eyebrow, "Plumber · Kagiso · since 2016")));

        var brow = html.IndexOf("Plumber", StringComparison.Ordinal);
        var title = html.IndexOf("<h1>", StringComparison.Ordinal);
        var words = html.IndexOf("Some words", StringComparison.Ordinal);

        Assert.True(brow > 0 && title > 0 && words > 0);
        Assert.True(brow < title, "the label came after the title");
        Assert.True(title < words, "the title came after the prose");
    }

    [Fact]
    public void A_label_is_never_drawn_twice()
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Eyebrow, "MARKER-ZZQ")));

        Assert.Equal(1, Occurrences(html, "MARKER-ZZQ"));
    }

    // ── Figures, quotes and breaks ────────────────────────────────────────────

    /// <summary>
    /// The first picture is the masthead. It is not a figure, and it carries no caption.
    /// </summary>
    /// <remarks>
    /// A page leads with its picture — that is what makes it read as a place rather than a document.
    /// So the first one is bled to the edges behind the title, and only the ones after it sit in the
    /// text as figures.
    /// </remarks>
    [Fact]
    public void The_first_picture_is_the_masthead()
    {
        var html = CardPage.Render(
            Card(new CardBlock { Kind = CardBlock.Image, ContentHash = "abc123", Value = "The shop, 1994" }),
            "Kagiso Plumbing", 0, downloadPath: null,
            assetPath: _ => "data:image/jpeg;base64,AAAA");

        Assert.Contains("plate-art", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<figure>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A picture in the body, with words under it, is a figure and the words are its caption.
    /// </summary>
    /// <remarks>
    /// The description used to go only into <c>alt</c>, where a sighted reader never saw it — so
    /// somebody who wrote "the shop on the corner, 1994" had written it for nobody.
    /// </remarks>
    [Fact]
    public void A_picture_in_the_body_is_a_figure_with_its_caption()
    {
        var html = CardPage.Render(
            Card(
                new CardBlock { Kind = CardBlock.Image, ContentHash = "hero111", Value = "" },
                new CardBlock { Kind = CardBlock.Image, ContentHash = "abc123", Value = "The shop, 1994" }),
            "Kagiso Plumbing", 0, downloadPath: null,
            assetPath: _ => "data:image/jpeg;base64,AAAA");

        Assert.Contains("<figure>", html, StringComparison.Ordinal);
        Assert.Contains("<figcaption>The shop, 1994</figcaption>", html, StringComparison.Ordinal);
        Assert.Contains("alt=\"The shop, 1994\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_with_nothing_written_under_it_gets_no_caption()
    {
        var html = CardPage.Render(
            Card(
                new CardBlock { Kind = CardBlock.Image, ContentHash = "hero111", Value = "" },
                new CardBlock { Kind = CardBlock.Image, ContentHash = "abc123", Value = "" }),
            "Kagiso Plumbing", 0, downloadPath: null,
            assetPath: _ => "data:image/jpeg;base64,AAAA");

        Assert.Contains("<figure>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<figcaption>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_is_set_apart_from_the_prose()
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Quote, "We come out on a Sunday.")));

        Assert.Contains("<blockquote>We come out on a Sunday.</blockquote>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A break carries no text, so the rule that drops empty blocks would have eaten it.
    /// </summary>
    [Fact]
    public void A_break_survives_having_nothing_to_say()
    {
        var card = Card(new CardBlock { Kind = CardBlock.Rule });

        Assert.Contains(OwnCard.ForPublish(card).Blocks, b => b.Kind == CardBlock.Rule);
        Assert.Contains("<hr>", Render(card), StringComparison.Ordinal);
    }

    // ── Everything an author can write, a reader can see ──────────────────────

    /// <summary>
    /// Every kind the editor offers is a kind the renderer draws.
    /// </summary>
    /// <remarks>
    /// The gap this closes is silent in the worst way: somebody fills in a block, publishes, and it
    /// simply is not on the page. They cannot tell whether they did something wrong or the network
    /// did, and the answer is neither.
    /// </remarks>
    [Fact]
    public void Every_kind_somebody_can_add_is_a_kind_that_draws()
    {
        foreach (var kind in OwnCard.Writable)
        {
            var block = kind switch
            {
                CardBlock.List or CardBlock.Index =>
                    new CardBlock { Kind = kind, Items = ["MARKER-ZZQ = here"] },
                CardBlock.Tip =>
                    new CardBlock { Kind = kind, Value = "MARKER-ZZQ", Target = "https://example.com/x" },
                CardBlock.Link =>
                    new CardBlock { Kind = kind, Value = "MARKER-ZZQ", Target = "aether://TAG/other" },
                // Two, because the first is the masthead and the second is the one in the text.
                CardBlock.Image =>
                    new CardBlock { Kind = kind, ContentHash = "abc123", Value = "MARKER-ZZQ" },
                CardBlock.Rule =>
                    new CardBlock { Kind = kind },
                _ => CardBlock.Of(kind, "MARKER-ZZQ"),
            };

            var blocks = kind == CardBlock.Image
                ? new[] { new CardBlock { Kind = kind, ContentHash = "hero111" }, block }
                : [block];

            var html = CardPage.Render(
                Card(blocks), "Kagiso Plumbing", 0, downloadPath: null,
                assetPath: _ => "data:image/jpeg;base64,AAAA");

            if (kind == CardBlock.Rule)
                Assert.Contains("<hr>", html, StringComparison.Ordinal);
            else
                Assert.True(html.Contains("MARKER-ZZQ", StringComparison.Ordinal), $"{kind} draws nothing");
        }
    }

    /// <summary>
    /// And every kind survives being published — the other half of the same gap.
    /// </summary>
    [Fact]
    public void Every_kind_somebody_fills_in_survives_publishing()
    {
        foreach (var kind in OwnCard.Writable)
        {
            var block = kind switch
            {
                CardBlock.List or CardBlock.Index => new CardBlock { Kind = kind, Items = ["A = 1"] },
                CardBlock.Tip => new CardBlock { Kind = kind, Value = "Tip", Target = "https://example.com/x" },
                CardBlock.Link => new CardBlock { Kind = kind, Value = "There", Target = "aether://TAG/other" },
                CardBlock.Image => new CardBlock { Kind = kind, ContentHash = "abc123", Value = "A picture" },
                CardBlock.Rule => new CardBlock { Kind = kind },
                CardBlock.KeyValue => CardBlock.Of(kind, "Open = Mon to Sat"),
                _ => CardBlock.Of(kind, "Something"),
            };

            Assert.Contains(OwnCard.ForPublish(Card(block)).Blocks, b => b.Kind == kind);
        }
    }

    // ── The measured system ───────────────────────────────────────────────────

    /// <summary>
    /// The numbers that make Editorial editorial, kept where somebody can see them change.
    /// </summary>
    /// <remarks>
    /// Measured off a page that already works rather than invented. The light weight and the air are
    /// the texture — the same words at weight 400 and leading 1.5 are a document, not an editorial
    /// page, and no test that only checked colours would notice.
    /// </remarks>
    [Fact]
    public void Editorial_is_a_light_serif_with_air_around_it()
    {
        var look = CardLook.Of("editorial");

        Assert.Equal(300, look.BodyWeight);
        Assert.True(look.Leading >= 1.7, $"leading is {look.Leading}");
        Assert.True(look.BodySize >= 17, $"body is {look.BodySize}px");
        Assert.Contains("Instrument Serif", look.Display, StringComparison.Ordinal);
        Assert.Contains("Newsreader", look.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every secondary tone is the ink at a lower alpha rather than a separate grey.
    /// </summary>
    /// <remarks>
    /// The single habit that makes a palette hold together. A grey mixed independently drifts away
    /// from the ground it sits on — on warm paper it reads as printing that has gone wrong, and it is
    /// almost impossible to see when you are choosing it.
    /// </remarks>
    [Fact]
    public void Dimmer_tones_are_the_ink_rather_than_a_grey()
    {
        var html = Render(Card(CardBlock.Of(CardBlock.Theme, "editorial")));

        Assert.Contains("--ink-2:color-mix", html, StringComparison.Ordinal);
        Assert.Contains("--ink-3:color-mix", html, StringComparison.Ordinal);
        Assert.Contains("--rule:color-mix", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Running text is held to a measure. A full-width paragraph is unreadable however good the
    /// typeface is, and a page that competes with websites has to know that.
    /// </summary>
    [Fact]
    public void Running_text_is_held_to_a_measure()
    {
        foreach (var look in CardLook.All)
            Assert.True(look.Measure is >= 28 and <= 40, $"{look.Key} measures {look.Measure}rem");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;

        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;

        return count;
    }
}
