// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The catalogue of backgrounds — the half of a page that decides whether anybody looks at it.
///
/// <para>
/// What separates a page somebody is proud of from one they settle for is usually not skill; it is how
/// many finished things there were to choose from. So the point of this is the <i>count</i>, and the
/// property that keeps the count growing: a background is a height field and nothing else, and adding
/// one is adding a record. If that ever stops being true, the catalogue stops growing, and a library
/// with one background produces one look forever.
/// </para>
///
/// <para>
/// Everything here fails quietly. A field with a typo in it compiles to nothing and the masthead comes
/// out flat; a key that never reaches the page means every card wears the default and nobody can tell
/// why their choice did nothing. Neither would announce itself.
/// </para>
/// </summary>
public class CardShaderTests
{
    private static string Render(string? shaderKey, string? lookKey = null)
    {
        var blocks = new List<CardBlock>();
        if (lookKey is not null) blocks.Add(CardBlock.Of(CardBlock.Theme, lookKey));
        if (shaderKey is not null) blocks.Add(CardBlock.Of(CardBlock.Theme, shaderKey));

        var card = new CardDocument { Title = "Kagiso Plumbing", Blocks = blocks };
        return CardPage.Render(card, card.Title, 0, downloadPath: null);
    }

    // ── The catalogue is a catalogue ──────────────────────────────────────────

    /// <summary>
    /// Enough to be a choice rather than a default.
    /// </summary>
    /// <remarks>
    /// The number is the feature. One background is a house style; a dozen is somewhere to browse, and
    /// browsing is what lets somebody who cannot design anything end up with a page they could not
    /// have made.
    /// </remarks>
    [Fact]
    public void There_are_enough_backgrounds_to_browse()
    {
        Assert.True(CardShader.All.Length >= 12, $"only {CardShader.All.Length} backgrounds");
    }

    [Fact]
    public void Every_background_is_a_different_one()
    {
        Assert.Equal(CardShader.All.Length, CardShader.All.Select(s => s.Key).Distinct().Count());
        Assert.Equal(CardShader.All.Length, CardShader.All.Select(s => s.Field).Distinct().Count());
    }

    [Fact]
    public void Every_background_says_what_it_is()
    {
        foreach (var back in CardShader.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(back.Name), $"{back.Key} has no name");
            Assert.False(string.IsNullOrWhiteSpace(back.Blurb), $"{back.Key} has no blurb");
        }
    }

    /// <summary>
    /// A background is a height field and nothing else.
    /// </summary>
    /// <remarks>
    /// The property the whole catalogue rests on. The painter supplies the uniforms, the normal, the
    /// lighting and the colour mix — so an entry cannot declare its own <c>main</c>, cannot take its
    /// own uniforms, and arrives already lit and already in the page's colour. That is what makes a
    /// developer's first contribution five lines long.
    /// </remarks>
    [Fact]
    public void Every_background_is_only_a_height_field()
    {
        foreach (var back in CardShader.All)
        {
            Assert.Contains("float field(vec2 p)", back.Field, StringComparison.Ordinal);
            Assert.DoesNotContain("void main", back.Field, StringComparison.Ordinal);
            Assert.DoesNotContain("uniform", back.Field, StringComparison.Ordinal);
            Assert.DoesNotContain("gl_FragColor", back.Field, StringComparison.Ordinal);
            Assert.DoesNotContain("precision", back.Field, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// And it is small enough to carry to a phone that has no internet.
    /// </summary>
    [Fact]
    public void Every_background_is_small_enough_to_travel()
    {
        foreach (var back in CardShader.All)
            Assert.True(back.Field.Length < 1400, $"{back.Key} is {back.Field.Length} bytes of GLSL");
    }

    /// <summary>
    /// Only the one it asked for. Inlining the whole catalogue into every page would be a page paying
    /// for thirteen backgrounds it will never draw.
    /// </summary>
    [Fact]
    public void A_page_carries_only_the_background_it_uses()
    {
        var html = Render("ribbon");

        Assert.Contains("aetherField", html, StringComparison.Ordinal);

        foreach (var other in CardShader.All.Where(s => s.Key != "ribbon"))
            Assert.DoesNotContain(Signature(other), html, StringComparison.Ordinal);
    }

    // ── A card names one, and gets it ─────────────────────────────────────────

    [Fact]
    public void Every_background_reaches_the_page_that_named_it()
    {
        foreach (var back in CardShader.All)
        {
            var html = Render(back.Key);
            Assert.True(html.Contains(Signature(back), StringComparison.Ordinal), $"{back.Key} never reaches the page");
        }
    }

    [Fact]
    public void A_page_that_names_nothing_gets_the_default()
    {
        Assert.Contains(Signature(CardShader.Of(CardShader.DefaultKey)), Render(null), StringComparison.Ordinal);
    }

    /// <summary>
    /// A background this build has never heard of falls back rather than failing — the same rule an
    /// unknown block and an unknown look already follow. A page written on a newer build still opens.
    /// </summary>
    [Fact]
    public void A_background_from_a_newer_build_falls_back()
    {
        var html = Render("iridescent-caustics-2029");

        Assert.Contains(Signature(CardShader.Of(CardShader.DefaultKey)), html, StringComparison.Ordinal);
    }

    // ── A look and a background are separate choices ──────────────────────────

    /// <summary>
    /// Both, at once, without either overruling the other.
    /// </summary>
    /// <remarks>
    /// They are two theme blocks, and the tidy-up keeps one of each rather than one in total —
    /// otherwise choosing a background silently threw away the typography, which is the kind of bug
    /// somebody discovers three edits later and cannot explain.
    /// </remarks>
    [Fact]
    public void A_look_and_a_background_are_both_kept()
    {
        var card = new CardDocument { Title = "Kagiso Plumbing" };

        OwnCard.SetLook(card, "editorial");
        OwnCard.SetShader(card, "ribbon");
        OwnCard.Tidy(card);

        Assert.Equal("editorial", CardLook.FromCard(card).Key);
        Assert.Equal("ribbon", CardShader.FromCard(card).Key);
    }

    [Fact]
    public void Choosing_a_second_background_replaces_the_first()
    {
        var card = new CardDocument();

        OwnCard.SetShader(card, "ribbon");
        OwnCard.SetShader(card, "halftone");
        OwnCard.Tidy(card);

        Assert.Equal("halftone", CardShader.FromCard(card).Key);
        Assert.Single(card.Blocks, b => b.Kind == CardBlock.Theme && CardShader.IsShader(b.Value));
    }

    [Fact]
    public void Changing_the_background_leaves_the_words_alone()
    {
        var card = new CardDocument
        {
            Title = "Kagiso Plumbing",
            Blocks = [CardBlock.Of(CardBlock.Text, "We come out on a Sunday.")],
        };

        OwnCard.SetShader(card, "dunes");

        Assert.Contains(card.Blocks, b => b.Kind == CardBlock.Text && b.Value == "We come out on a Sunday.");
    }

    /// <summary>
    /// Every background takes the colour of the page it is on rather than bringing its own.
    /// </summary>
    /// <remarks>
    /// It is why fourteen backgrounds across five looks are seventy finished pages rather than
    /// fourteen — and why a new entry needs no colour decisions at all.
    /// </remarks>
    [Fact]
    public void A_background_takes_the_colour_of_the_page_it_is_on()
    {
        foreach (var look in CardLook.All)
        {
            var html = Render("flow", look.Key);
            Assert.Contains($"data-accent=\"{look.Accent}\"", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// No entry names a colour of its own. One that did would be the one background that looked
    /// wrong on four looks out of five, and nothing else in the catalogue would warn you.
    /// </summary>
    [Fact]
    public void No_background_brings_its_own_colour()
    {
        foreach (var back in CardShader.All)
        {
            Assert.DoesNotContain("u_bg", back.Field, StringComparison.Ordinal);
            Assert.DoesNotContain("#", back.Field, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An entry exactly as the page carries it.
    /// </summary>
    /// <remarks>
    /// Serialised, because that is the form on the page. The encoder escapes characters that could end
    /// a script tag, so the GLSL on the page is not character-for-character the GLSL in the source —
    /// and a test comparing the two directly fails for the wrong reason.
    /// </remarks>
    private static string Signature(CardShader back) =>
        System.Text.Json.JsonSerializer.Serialize(back.Field);
}
