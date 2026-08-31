// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The author's own stylesheet — everything allowed except the things that reach.
///
/// <para>
/// Sliders are not coding. MySpace taught people CSS because the page was theirs to break, and a card
/// nobody can take apart teaches nothing. So an author writes real CSS. What they may not do is make
/// a request from somebody else's phone, or paint outside their own card.
/// </para>
/// </summary>
public class CardCssTests
{
    // ── What an author may do, which is nearly everything ──────────────────────

    [Fact]
    public void Ordinary_css_survives()
    {
        var css = CardCss.Safe("h1 { color: #ff0090; letter-spacing: -.04em; transform: rotate(-2deg) }");

        Assert.Contains("color: #ff0090", css);
        Assert.Contains("rotate(-2deg)", css);
    }

    /// <summary>Including the ugly, which is the point.</summary>
    [Fact]
    public void Nothing_is_refused_for_being_in_bad_taste()
    {
        var css = CardCss.Safe("p { font-size: 90px; text-shadow: 3px 3px lime; background: fuchsia }");

        Assert.Contains("90px", css);
        Assert.Contains("fuchsia", css);
    }

    /// <summary>A card that cannot answer a narrow screen cannot be responsive.</summary>
    [Fact]
    public void A_media_query_is_walked_into_not_flattened()
    {
        var css = CardCss.Safe("@media (max-width: 500px) { h1 { font-size: 20px } }");

        Assert.Contains("@media (max-width: 500px)", css);
        Assert.Contains($"{CardCss.Root} h1", css);
    }

    /// <summary>Keyframe steps are percentages, not selectors.</summary>
    [Fact]
    public void An_animation_still_animates()
    {
        var css = CardCss.Safe("@keyframes drift { 0% { opacity: 0 } 100% { opacity: 1 } }");

        Assert.Contains("@keyframes drift", css);
        Assert.DoesNotContain($"{CardCss.Root} 0%", css);
        Assert.Contains("0% { opacity: 0 }", css.Replace("  ", " "));
    }

    // ── What it may not do ─────────────────────────────────────────────────────

    /// <summary>
    /// A stylesheet may not make a request.
    /// </summary>
    /// <remarks>
    /// This is the one that matters most on this network. A single <c>url()</c> in a card handed to a
    /// stranger is a tracking pixel: it tells the author's server that this person opened this card,
    /// from this address, at this time — on a network whose entire promise is that nobody is watching.
    /// It goes wholesale rather than by scheme, because "only https" becomes an argument about
    /// redirects, then about data:, then about nothing at all.
    /// </remarks>
    [Theory]
    [InlineData("body { background: url(https://tracker.example/p.gif) }")]
    [InlineData("body { background: URL('//evil/x') }")]
    [InlineData("@import url('https://evil/x.css');")]
    [InlineData("@import 'https://evil/x.css';")]
    [InlineData("a { background-image: url( data:image/gif;base64,AAAA ) }")]
    public void A_stylesheet_cannot_fetch(string written)
    {
        var css = CardCss.Safe(written);

        Assert.DoesNotContain("url(", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker", css, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a { width: expression(alert(1)) }")]
    [InlineData("a { -moz-binding: url(x.xml#y) }")]
    [InlineData("a { behavior: url(x.htc) }")]
    [InlineData("a { background: javascript:alert(1) }")]
    public void A_stylesheet_cannot_execute(string written)
    {
        var css = CardCss.Safe(written);

        Assert.DoesNotContain("expression(", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("binding", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("behavior", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", css, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And it cannot paint anything that is not the card.
    /// </summary>
    /// <remarks>
    /// Without this, a card handed to you can blank the app with <c>body { display: none }</c> or
    /// repaint the navigation with <c>.tabbar { }</c> — a stranger's page dressing itself up as your
    /// phone. Every selector is rooted; <c>body</c> and <c>:root</c> are read as "my own page", which
    /// is what the author meant.
    /// </remarks>
    [Theory]
    [InlineData(".tabbar { display: none }")]
    [InlineData("* { color: red }")]
    [InlineData("div.appbar span { opacity: 0 }")]
    public void A_stylesheet_cannot_reach_out_of_its_card(string written)
    {
        foreach (var rule in CardCss.Safe(written).Split('}', StringSplitOptions.RemoveEmptyEntries))
        {
            var head = rule.Split('{')[0].Trim();
            if (head.Length == 0 || head.StartsWith('@')) continue;

            foreach (var one in head.Split(','))
                Assert.StartsWith(CardCss.Root, one.Trim());
        }
    }

    [Theory]
    [InlineData("body { background: #000 }")]
    [InlineData(":root { color: #fff }")]
    [InlineData("html { margin: 0 }")]
    public void Meaning_my_own_page_is_not_an_attack(string written)
    {
        var css = CardCss.Safe(written);

        Assert.StartsWith(CardCss.Root + " {", css.Trim());
    }

    // ── Not breaking the reader ───────────────────────────────────────────────

    /// <summary>
    /// A malformed stylesheet costs its own author, never the reader.
    /// </summary>
    /// <remarks>
    /// A card is not a form submission — it arrives over a radio from somebody who is not present to
    /// be told. A reader whose page went blank because a stranger's stylesheet had a stray brace has
    /// been failed by us.
    /// </remarks>
    [Theory]
    [InlineData("h1 { color: red")]
    [InlineData("} h1 { color: red }")]
    [InlineData("")]
    [InlineData(null)]
    public void A_broken_stylesheet_is_dropped_rather_than_thrown(string? written)
        => Assert.NotNull(CardCss.Safe(written));

    [Fact]
    public void A_stylesheet_longer_than_a_card_should_carry_is_cut()
        => Assert.True(CardCss.Safe("h1 { color: red } " + new string('x', 40_000)).Length <= CardCss.Most + 512);
}

/// <summary>
/// The author's stylesheet, as it actually reaches a reader's page.
/// </summary>
/// <remarks>
/// The unit tests above prove the sanitiser. These prove the wiring — that what a person writes is
/// stored as they wrote it, arrives in the rendered page, comes after our own styles so their rule
/// wins on their own card, and still cannot reach past it.
/// </remarks>
public class CardCssPageTests
{
    private static string Rendered(string written)
    {
        var card = PageTemplate.Of("blank").Build("Someone");
        OwnCard.SetCss(card, written);
        return CardPage.Render(card, "A page", 0, downloadPath: null);
    }

    [Fact]
    public void What_the_author_wrote_is_kept_as_they_wrote_it()
    {
        var card = PageTemplate.Of("blank").Build(null);
        OwnCard.SetCss(card, "h1 { background: url(https://x/y.png) }");

        Assert.Equal("h1 { background: url(https://x/y.png) }",
            card.Blocks.First(b => b.Kind == CardBlock.Css).Value);
    }

    [Fact]
    public void And_reaches_the_page_confined_to_the_card()
    {
        var page = Rendered("h1 { color: #ff0090 }");

        Assert.Contains("card-own", page);
        Assert.Contains($"{CardCss.Root} h1", page);
        Assert.Contains("#ff0090", page);
    }

    /// <summary>Their rule comes after ours, or writing one would be pointless.</summary>
    [Fact]
    public void The_authors_stylesheet_is_the_last_word_on_their_own_page()
    {
        var page = Rendered("h1 { color: #ff0090 }");

        Assert.True(page.LastIndexOf("#ff0090", StringComparison.Ordinal)
                  > page.IndexOf("</style>", StringComparison.Ordinal));
    }

    /// <summary>But never past it, however it arrived.</summary>
    [Fact]
    public void A_card_from_the_radio_still_cannot_fetch_or_reach_out()
    {
        var page = Rendered(".tabbar { display: none } body { background: url(https://tracker/p.gif) }");

        Assert.DoesNotContain("tracker", page);
        Assert.DoesNotContain($"}}.tabbar", page.Replace(" ", ""));
        Assert.Contains($"{CardCss.Root} .tabbar", page);
    }

    /// <summary>A card with no stylesheet carries no empty tag.</summary>
    [Fact]
    public void Writing_nothing_adds_nothing()
    {
        var card = PageTemplate.Of("blank").Build(null);
        OwnCard.SetCss(card, "   ");

        Assert.DoesNotContain(card.Blocks, b => b.Kind == CardBlock.Css);
    }
}
