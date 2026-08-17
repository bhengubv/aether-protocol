// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The blocks that make a card look like a place rather than a document.
///
/// <para>
/// A card of headings and bullets is honest and safe and nobody wants to look at it. To stand next to a
/// web page it needs a picture and an identity of its own — but it must gain those <b>without</b> gaining
/// the two things the card model exists to refuse: fetching from the open web, and running code.
/// </para>
///
/// <para>
/// So an image is carried as a <b>content hash</b>, never a URL: the bytes come from the mesh, which is
/// what lets a card render years later, offline, with its author long gone. And a theme is a small set
/// of <b>declared</b> choices the renderer interprets — never CSS, never markup — so a stranger's card
/// can look like itself without being able to restyle the app around it.
/// </para>
///
/// <para>
/// The bandwidth matters here: on a BLE link measured at roughly 5 kbps, a 50 KB photo is eighty
/// seconds. Card art has to be small by design, not merely compressed.
/// </para>
/// </summary>
public class CardRichBlockTests
{
    // ── Images ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_image_block_survives_being_written_and_read()
    {
        var card = new CardDocument
        {
            Title = "Spaza",
            Blocks = [new CardBlock { Kind = CardBlock.Image, ContentHash = "b1946ac9", Value = "The shop front" }],
        };

        var read = CardDocument.Parse(card.ToJson())!.Blocks.Single();

        Assert.Equal(CardBlock.Image, read.Kind);
        Assert.Equal("b1946ac9", read.ContentHash);
        Assert.Equal("The shop front", read.Value);
    }

    /// <summary>
    /// The whole point of a content hash. A card that could name an <c>http</c> address would phone
    /// home the instant a stranger opened it, and would stop working the day that host went away.
    /// </summary>
    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("https://example.com/a.png")]
    [InlineData("//example.com/a.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    public void An_image_that_names_somewhere_to_fetch_from_is_not_a_usable_hash(string notAHash)
    {
        Assert.False(CardBlock.IsUsableAssetHash(notAHash));
    }

    [Theory]
    [InlineData("b1946ac92492d2347c6235b4d2611184")]
    [InlineData("B1946AC9")]
    public void A_plain_content_hash_is_usable(string hash)
    {
        Assert.True(CardBlock.IsUsableAssetHash(hash));
    }

    [Fact]
    public void An_image_with_no_hash_is_not_usable() =>
        Assert.False(CardBlock.IsUsableAssetHash(null));

    // ── Theme ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_theme_block_survives_being_written_and_read()
    {
        var card = new CardDocument
        {
            Title = "Spaza",
            Blocks = [new CardBlock { Kind = CardBlock.Theme, Value = "#7A4B2A" }],
        };

        var read = CardDocument.Parse(card.ToJson())!.Blocks.Single();

        Assert.Equal(CardBlock.Theme, read.Kind);
        Assert.Equal("#7A4B2A", read.Value);
    }

    /// <summary>
    /// A theme is a colour this renderer chooses to honour — not a style sheet. Anything that is not a
    /// plain hex colour is refused, so a card cannot smuggle CSS in through the accent.
    /// </summary>
    [Theory]
    [InlineData("#7A4B2A")]
    [InlineData("#abc")]
    public void A_plain_hex_colour_is_an_acceptable_accent(string colour) =>
        Assert.True(CardBlock.IsUsableAccent(colour));

    [Theory]
    [InlineData("red; position:fixed; top:0")]
    [InlineData("url(http://x/y)")]
    [InlineData("expression(alert(1))")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_hex_colour_is_refused(string? notAColour) =>
        Assert.False(CardBlock.IsUsableAccent(notAColour));

    // ── A card that uses everything ───────────────────────────────────────────

    [Fact]
    public void A_card_can_carry_every_kind_at_once()
    {
        var card = new CardDocument
        {
            Title = "Everything",
            Blocks =
            [
                new CardBlock { Kind = CardBlock.Theme, Value = "#7A4B2A" },
                new CardBlock { Kind = CardBlock.Image, ContentHash = "b1946ac9", Value = "Shop front" },
                CardBlock.Of(CardBlock.Heading, "Today"),
                CardBlock.Of(CardBlock.Text, "Open until eight."),
                new CardBlock { Kind = CardBlock.List, Items = ["Bread", "Milk"] },
                new CardBlock { Kind = CardBlock.KeyValue, Value = "Bread · R18" },
                new CardBlock { Kind = CardBlock.Link, Value = "Prices", Target = "aether://KXJB7-MN2P4/prices" },
            ],
        };

        var read = CardDocument.Parse(card.ToJson())!;

        Assert.Equal(7, read.Blocks.Count);
        Assert.Equal(
            [CardBlock.Theme, CardBlock.Image, CardBlock.Heading, CardBlock.Text, CardBlock.List, CardBlock.KeyValue, CardBlock.Link],
            read.Blocks.Select(b => b.Kind));
    }
}
