// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The same page, on the mesh and on the open web.
///
/// <para>
/// Somebody who writes a page on their phone should be able to have it at their own domain <i>and</i>
/// on AetherNet, and have them be the same page — not similar, not a port. That is only true if there
/// is one document and one renderer, and it stops being true the moment anybody adds a second code
/// path "just for the web version".
/// </para>
///
/// <para>
/// So these tests compare the two outputs directly. They would pass just as well if somebody wrote a
/// separate exporter that happened to agree today — which is exactly the failure worth catching,
/// because that agreement lasts until the first change to either side and then drifts silently.
/// </para>
/// </summary>
public class CardExportTests
{
    private static CardDocument Card() => new()
    {
        Title = "Kagiso Plumbing",
        Blocks =
        [
            CardBlock.Of(CardBlock.Theme, "editorial"),
            CardBlock.Of(CardBlock.Theme, "tide"),
            CardBlock.Of(CardBlock.Eyebrow, "Plumber · Kagiso · since 2016"),
            CardBlock.Of(CardBlock.Text, "We come out on a Sunday."),
            new CardBlock { Kind = CardBlock.Index, Items = ["Geyser = From R2400", "Drain = From R650"] },
            new CardBlock { Kind = CardBlock.Rule, Value = "brush" },
            CardBlock.Of(CardBlock.KeyValue, "Call = 082 000 0000"),
        ],
    };

    private static string? Picture(string hash) => "data:image/jpeg;base64,AAAA";

    // ── One renderer, two destinations ────────────────────────────────────────

    /// <summary>
    /// What a reader gets on the web is what a reader gets on the mesh.
    /// </summary>
    /// <remarks>
    /// Byte for byte. There is one renderer and one document, so there is nothing to compare
    /// loosely — and a second code path "just for the web version" would show up here immediately.
    /// </remarks>
    [Fact]
    public void The_exported_page_is_the_page_the_mesh_serves()
    {
        var onTheMesh = CardPage.Render(
            Card(), "Kagiso Plumbing", 0, downloadPath: null, assetPath: Picture, fonts: PageAssets.Face);

        var onTheWeb = CardExport.Standalone(Card(), Picture);

        Assert.Equal(onTheMesh, onTheWeb);
    }

    /// <summary>
    /// The web copy adds nothing at all — not even a line saying where else the page lives.
    /// </summary>
    /// <remarks>
    /// It used to append one. A sentence is still a difference, and an "identical" that tolerates a
    /// sentence tolerates the next thing too. If a reader on the web should be told about the mesh
    /// copy, the author writes that line and it appears on both.
    /// </remarks>
    [Fact]
    public void The_web_copy_adds_nothing()
    {
        var plain = CardExport.Standalone(Card(), Picture);
        var asked = CardExport.Standalone(Card(), Picture, at: "aether://Y6TK9-EW9KK/shop");

        Assert.Equal(plain, asked);
        Assert.DoesNotContain("aether://Y6TK9-EW9KK/shop", asked, StringComparison.Ordinal);
    }

    // ── One file, and it keeps working ────────────────────────────────────────

    /// <summary>
    /// Nothing beside it, and nothing it reaches for.
    /// </summary>
    /// <remarks>
    /// The property that makes this worth doing at all: a page that needs a server is a page somebody
    /// can take away, and the reason a card is worth writing is that nobody can. An export that
    /// linked a stylesheet or a font would carry that fragility straight onto the web.
    /// </remarks>
    [Fact]
    public void The_exported_page_reaches_for_nothing()
    {
        var page = CardExport.Standalone(Card(), Picture, at: "aether://TAG/shop");

        Assert.DoesNotContain("http://", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script src", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_typefaces_travel_with_it()
    {
        var page = CardExport.Standalone(Card(), Picture);

        Assert.Contains("@font-face", page, StringComparison.Ordinal);
        Assert.Contains("data:font/woff2;base64,", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pictures_travel_with_it()
    {
        var card = Card();
        card.Blocks.Add(new CardBlock { Kind = CardBlock.Image, ContentHash = "abc123", Value = "The van" });

        Assert.Contains("data:image/jpeg;base64,", CardExport.Standalone(card, Picture), StringComparison.Ordinal);
    }

    /// <summary>
    /// It opens from a file, which is what "no server" has to mean.
    /// </summary>
    /// <remarks>
    /// A page that only works when something serves it is not a file, it is a deployment. Every URL in
    /// this document is a data: URI or an anchor the reader chooses to follow, so nothing about it
    /// depends on where it sits.
    /// </remarks>
    [Fact]
    public void It_does_not_depend_on_where_it_sits()
    {
        var page = CardExport.Standalone(Card(), Picture);

        Assert.DoesNotContain("src=\"/", page, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/", page, StringComparison.Ordinal);
        Assert.DoesNotContain("url(/", page, StringComparison.Ordinal);
    }

    // ── What it costs ─────────────────────────────────────────────────────────

    /// <summary>
    /// A page of text is a few kilobytes; the pictures are the file.
    /// </summary>
    /// <remarks>
    /// Base64 costs a third on top, so somebody about to put this on a host they pay for should be
    /// told the number before they write the file rather than after.
    /// </remarks>
    [Fact]
    public void The_size_can_be_known_before_the_file_is_written()
    {
        var weighed = CardExport.Weigh(Card(), Picture);
        var actual = Encoding.UTF8.GetByteCount(CardExport.Standalone(Card(), Picture));

        Assert.Equal(actual, weighed);
    }

    [Fact]
    public void A_page_with_no_pictures_is_small_enough_to_email()
    {
        var page = CardExport.Standalone(Card());

        // Under a quarter of a megabyte, and almost all of that is the two typefaces.
        Assert.True(Encoding.UTF8.GetByteCount(page) < 260 * 1024,
            $"{Encoding.UTF8.GetByteCount(page) / 1024} KB");
    }

    [Theory]
    [InlineData("shop", "shop.html")]
    [InlineData("My Links", "my-links.html")]
    [InlineData("", "card.html")]
    [InlineData(null, "card.html")]
    public void The_file_is_named_after_the_page(string? name, string expected) =>
        Assert.Equal(expected, CardExport.FileName(name));
}
