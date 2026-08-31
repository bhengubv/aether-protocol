// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A card that changed its own look — the dials, not the machine.
///
/// <para>
/// MySpace taught a generation typography and colour because the page you had just been handed could
/// be opened and changed, one value at a time. Five finished looks in a picker cannot do that. What
/// can is the page carrying its own type, measure, ground and ink — the same values a designer sets
/// in a stylesheet — while the renderer stays ours, so a card still cannot fetch, execute, or escape
/// its own page on a stranger's phone.
/// </para>
/// </summary>
public class CardStyleTests
{
    private static CardDocument With(params string[] dials)
    {
        var card = PageTemplate.Of("blank").Build(null);
        card.Blocks.Insert(0, new CardBlock { Kind = CardBlock.Theme, Value = "editorial" });
        card.Blocks.Insert(1, new CardBlock { Kind = CardBlock.Style, Items = [.. dials] });
        return card;
    }

    /// <summary>A card with no dials is exactly the look it named.</summary>
    [Fact]
    public void A_card_that_changed_nothing_is_unchanged()
    {
        var plain = CardLook.Of("editorial");
        var card = PageTemplate.Of("blank").Build(null);
        card.Blocks.Insert(0, new CardBlock { Kind = CardBlock.Theme, Value = "editorial" });

        Assert.Equal(plain, CardLook.FromCard(card));
    }

    /// <summary>And one that turned a dial gets that, and keeps everything else.</summary>
    [Fact]
    public void One_dial_turned_leaves_the_rest_of_the_look_alone()
    {
        var from = CardLook.Of("editorial");
        var look = CardLook.FromCard(With("measure = 28"));

        Assert.Equal(28, look.Measure);
        Assert.Equal(from.Ink, look.Ink);
        Assert.Equal(from.BodySize, look.BodySize);
        Assert.Equal(from.Display, look.Display);
    }

    [Fact]
    public void The_dials_that_make_a_page_read_differently_all_work()
    {
        var look = CardLook.FromCard(With(
            "display = mono", "body = reader", "paper = #101014", "ink = #f2efe6",
            "accent = #c8a24a", "weight = 500", "size = 19", "leading = 1.9", "measure = 34"));

        Assert.Contains("monospace", look.Display);
        Assert.Contains("Newsreader", look.Body);
        Assert.Equal("#101014", look.Paper);
        Assert.Equal("#f2efe6", look.Ink);
        Assert.Equal("#c8a24a", look.Accent);
        Assert.Equal(500, look.BodyWeight);
        Assert.Equal(19, look.BodySize);
        Assert.Equal(1.9, look.Leading);
        Assert.Equal(34, look.Measure);
    }

    /// <summary>
    /// A colour field is a plain hex value and nothing else.
    /// </summary>
    /// <remarks>
    /// This is the one that matters. A colour field that accepts CSS accepts <c>url(...)</c>, which is
    /// a card phoning home from a stranger's phone, and it is the exact hole the JSON model was
    /// chosen to close. Named colours go too — "red" is harmless and the grammar that admits it is
    /// not.
    /// </remarks>
    [Theory]
    [InlineData("url(https://tracker.example/x.png)")]
    [InlineData("red")]
    [InlineData("expression(alert(1))")]
    [InlineData("#fff; background-image: url(x)")]
    [InlineData("var(--ink)")]
    [InlineData("rgb(0,0,0)")]
    public void A_colour_that_is_not_a_hex_value_is_refused(string smuggled)
    {
        var from = CardLook.Of("editorial");
        Assert.Equal(from.Paper, CardLook.FromCard(With($"paper = {smuggled}")).Paper);
        Assert.False(CardLook.IsColour(smuggled));
    }

    /// <summary>And a typeface is a name we offer, never a stack the author wrote.</summary>
    [Theory]
    [InlineData("'Evil', url(https://x/y.woff2)")]
    [InlineData("Comic Sans MS")]
    [InlineData("")]
    public void A_typeface_a_card_invents_is_refused(string invented)
    {
        Assert.False(Typeface.IsOffered(invented));
        Assert.Equal(CardLook.Of("editorial").Body, CardLook.FromCard(With($"body = {invented}")).Body);
    }

    /// <summary>
    /// Numbers are clamped to where the design still works, not taken as given.
    /// </summary>
    /// <remarks>
    /// Somebody dragging a slider to the end should get the end. A measure of 900rem arriving from
    /// another implementation should get the widest measure that still reads, not a page one line
    /// long and a mile wide.
    /// </remarks>
    [Fact]
    public void A_number_past_the_end_of_the_dial_lands_on_the_end()
    {
        Assert.Equal(60, CardLook.FromCard(With("measure = 900")).Measure);
        Assert.Equal(900, CardLook.FromCard(With("weight = 40000")).BodyWeight);
        Assert.Equal(12, CardLook.FromCard(With("size = -4")).BodySize);
    }

    /// <summary>A dial nobody has heard of is ignored rather than fatal.</summary>
    [Fact]
    public void A_dial_from_a_newer_app_is_skipped()
    {
        var look = CardLook.FromCard(With("shadow = 4", "measure = 30", "= 9", "nonsense"));

        Assert.Equal(30, look.Measure);
        Assert.Equal(CardLook.Of("editorial").Ink, look.Ink);
    }
}
