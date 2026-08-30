// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Making one like somebody else's.
///
/// <para>
/// <b>This is the gesture the whole thing is for.</b> A generation learned HTML and CSS off MySpace
/// and nobody taught them: they saw a page they liked, looked at how it was made, copied it, changed
/// the colours, broke it, fixed it. Wanting the aesthetic was the lesson plan. Cards already travel
/// from phone to phone, so the trading is not delivery — it is the lesson, and this is the half of it
/// that was missing.
/// </para>
///
/// <para>
/// So what a remix carries and what it leaves behind is not a detail. Carry too little and somebody
/// gets a blank page and learns nothing; carry too much and they publish a stranger's photographs
/// under their own name, or a stranger's payment address.
/// </para>
/// </summary>
public class CardRemixTests
{
    private static CardDocument Theirs() => new()
    {
        Title = "Meng To",
        Blocks =
        [
            CardBlock.Of(CardBlock.Theme, "editorial"),
            CardBlock.Of(CardBlock.Theme, "tide"),
            new CardBlock { Kind = CardBlock.Title, Value = "Meng To", As = "small", Align = "centre" },
            new CardBlock { Kind = CardBlock.Link, Value = "Journal", Target = "aether://MENGTO/journal" },
            CardBlock.Of(CardBlock.Eyebrow, "Designer / Creator / AI Educator"),
            new CardBlock { Kind = CardBlock.Image, Value = "Marina Bay", ContentHash = "abc123", As = "wide" },
            CardBlock.Of(CardBlock.Text, "The city looked at slowly, *in ink and a little colour*."),
            new CardBlock { Kind = CardBlock.Index, Items = ["Marina Bay Sands = Bayfront"] },
            new CardBlock { Kind = CardBlock.Rule, Value = "brush" },
            new CardBlock { Kind = CardBlock.Tip, Value = "Buy me a coffee", Target = "https://buymeacoffee.com/mengto" },
        ],
    };

    // ── What comes across ─────────────────────────────────────────────────────

    /// <summary>
    /// The design comes across, because the design is the thing somebody wants.
    /// </summary>
    /// <remarks>
    /// "One like this" means the look, the background, and the order of the page. A remix that kept
    /// only the words would hand somebody a blank page and a shrug.
    /// </remarks>
    [Fact]
    public void The_look_and_the_background_come_across()
    {
        var mine = OwnCard.Remix(Theirs(), "Kagiso Plumbing");

        Assert.Equal("editorial", OwnCard.LookOf(mine).Key);
        Assert.Equal("tide", OwnCard.ShaderOf(mine).Key);
    }

    /// <summary>Every piece, in the order they had it.</summary>
    [Fact]
    public void The_shape_of_the_page_comes_across()
    {
        var theirs = Theirs();
        var mine = OwnCard.Remix(theirs, "Kagiso Plumbing");

        Assert.Equal(
            theirs.Blocks.Select(b => b.Kind),
            mine.Blocks.Select(b => b.Kind));
    }

    /// <summary>
    /// And how each piece was set — centred, wide, a wordmark rather than a headline.
    /// </summary>
    /// <remarks>
    /// This is where the learning actually is. Somebody who gets the blocks but not the way they were
    /// set gets a page with the same words in it and none of the reason it looked good.
    /// </remarks>
    [Fact]
    public void How_each_piece_was_set_comes_across()
    {
        var mine = OwnCard.Remix(Theirs(), "Kagiso Plumbing");

        var titled = Assert.Single(mine.Blocks, b => b.Kind == CardBlock.Title);
        Assert.True(titled.IsSmall, "the wordmark came across as an ordinary headline");
        Assert.True(titled.IsCentred, "the centring did not come across");

        Assert.True(Assert.Single(mine.Blocks, b => b.Kind == CardBlock.Image).IsWide);
        Assert.Equal("brush", Assert.Single(mine.Blocks, b => b.Kind == CardBlock.Rule).Value);
    }

    /// <summary>
    /// The words come across as something to type over.
    /// </summary>
    /// <remarks>
    /// Not as a blank page. A template that says nothing is a page most people abandon halfway, and
    /// somebody who can see what the sentence was for can write their own version of it.
    /// </remarks>
    [Fact]
    public void The_words_come_across_to_be_typed_over()
    {
        var mine = OwnCard.Remix(Theirs(), "Kagiso Plumbing");

        Assert.Contains(mine.Blocks, b => b.Value?.Contains("in ink and a little colour") == true);
        Assert.Contains(mine.Blocks, b => b.Items?.Contains("Marina Bay Sands = Bayfront") == true);
    }

    /// <summary>The lists are copied, not shared.</summary>
    /// <remarks>
    /// Two documents pointing at one list is a page that changes when somebody edits a different page.
    /// </remarks>
    [Fact]
    public void Nothing_is_still_attached_to_the_page_it_came_from()
    {
        var theirs = Theirs();
        var mine = OwnCard.Remix(theirs, "Kagiso Plumbing");

        mine.Blocks.First(b => b.Kind == CardBlock.Index).Items!.Add("Geyser = From R2400");

        Assert.Single(theirs.Blocks.First(b => b.Kind == CardBlock.Index).Items!);
    }

    // ── What stays with its author ────────────────────────────────────────────

    /// <summary>
    /// Their photographs do not come across.
    /// </summary>
    /// <remarks>
    /// The frame does. A page that read well with a picture in it still reads that way, and the
    /// picture is the one thing on the page that is unmistakably somebody's — so the slot arrives
    /// empty and it is filled by whoever is making the page now.
    /// </remarks>
    [Fact]
    public void Their_photographs_stay_with_them()
    {
        var mine = OwnCard.Remix(Theirs(), "Kagiso Plumbing");
        var picture = Assert.Single(mine.Blocks, b => b.Kind == CardBlock.Image);

        Assert.Null(picture.ContentHash);
        Assert.Equal("Marina Bay", picture.Value);
    }

    /// <summary>
    /// And neither does their tip jar.
    /// </summary>
    /// <remarks>
    /// The one that would be worst to get wrong, and the one nobody would notice: a remixed page that
    /// quietly kept the original author's payment address sends a stranger's money to somebody who
    /// never asked for it, and it looks exactly like a page working properly.
    /// </remarks>
    [Fact]
    public void Their_tip_jar_stays_with_them()
    {
        var mine = OwnCard.Remix(Theirs(), "Kagiso Plumbing");
        var jar = Assert.Single(mine.Blocks, b => b.Kind == CardBlock.Tip);

        Assert.Null(jar.Target);
    }

    /// <summary>
    /// And their pages are not where your navigation goes.
    /// </summary>
    /// <remarks>
    /// The same failure as the tip jar, and just as quiet. A nav row saying Journal / About / Contact
    /// is exactly the thing somebody wants copied — pointed at the author's tag, it sends your readers
    /// to them, and everything about the page looks like it is working. The words come across; where
    /// they go does not.
    /// </remarks>
    [Fact]
    public void Their_pages_are_not_where_your_navigation_goes()
    {
        var mine = OwnCard.Remix(Theirs(), "Kagiso Plumbing");
        var nav = Assert.Single(mine.Blocks, b => b.Kind == CardBlock.Link);

        Assert.Equal("Journal", nav.Value);
        Assert.Null(nav.Target);
        Assert.DoesNotContain("MENGTO", OwnCard.ForPublish(mine).ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An address on the open web does come across.
    /// </summary>
    /// <remarks>
    /// A reference rather than a destination somebody owns — the link inside a sentence is part of
    /// what was written, and the writing comes across to be typed over.
    /// </remarks>
    [Fact]
    public void A_reference_to_the_open_web_comes_across()
    {
        var theirs = Theirs();
        theirs.Blocks.Add(new CardBlock
        {
            Kind = CardBlock.Link, Value = "Design+Code", Target = "https://designcode.io",
        });

        var mine = OwnCard.Remix(theirs, "Kagiso Plumbing");

        Assert.Equal(
            "https://designcode.io",
            mine.Blocks.Last(b => b.Kind == CardBlock.Link).Target);
    }

    /// <summary>Their name is not on the page any more either.</summary>
    [Fact]
    public void The_page_is_named_by_whoever_made_it()
    {
        var mine = OwnCard.Remix(Theirs(), "Kagiso Plumbing");

        Assert.Equal("Kagiso Plumbing", mine.Title);
        Assert.Equal("Kagiso Plumbing", Assert.Single(mine.Blocks, b => b.Kind == CardBlock.Title).Value);
    }

    /// <summary>A remix is a card like any other, so every rule a card lives under still applies.</summary>
    [Fact]
    public void A_remix_is_tidied_like_anything_else()
    {
        var huge = new CardDocument
        {
            Blocks = [.. Enumerable.Range(0, OwnCard.MostBlocks + 20)
                                   .Select(i => CardBlock.Of(CardBlock.Text, $"Line {i}"))],
        };

        Assert.True(OwnCard.Remix(huge, "Mine").Blocks.Count <= OwnCard.MostBlocks);
    }

    [Fact]
    public void Remixing_nothing_gives_an_empty_page_rather_than_a_crash()
    {
        var mine = OwnCard.Remix(null, "Mine");

        Assert.Equal("Mine", mine.Title);
        Assert.NotNull(mine.Blocks);
    }
}
