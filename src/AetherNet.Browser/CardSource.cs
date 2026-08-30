// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace AetherNet.Browser;

/// <summary>
/// How a page is made — readable by whoever is holding it.
///
/// <para>
/// <b>This is the point of the whole thing.</b> A generation learned HTML and CSS off MySpace, and
/// nobody taught them: they saw a profile they liked, went and looked at how it was made, copied it,
/// changed the colours, broke it, and fixed it. Wanting the aesthetic was the lesson plan. Cards
/// already travel from phone to phone, so the trading is not delivery — it is the lesson, and this is
/// the part that was missing from it.
/// </para>
///
/// <para>
/// <b>Four layers, and a person can stop at any of them.</b> The blocks a page is built from. The
/// look, as the handful of numbers and colours it actually is. The background, as the function that
/// draws it. And the document itself, which is small enough to read and belongs to whoever holds it.
/// Somebody who goes all the way down has learned structured content, design tokens and a fragment
/// shader on a handset, and nobody called it a lesson.
/// </para>
///
/// <para>
/// <b>Data, not markup.</b> This describes a card; it does not draw one. A host shows these however
/// it likes — the sample shows them in a panel, an operating system might show them another way — and
/// none of it is coupled to a screen.
/// </para>
/// </summary>
/// <param name="Pieces">What the page is built from, in the order a reader meets them.</param>
/// <param name="Look">The look's key, as the document stores it.</param>
/// <param name="LookName">What that look is called.</param>
/// <param name="Tokens">The look, as the values it actually is.</param>
/// <param name="Back">The background's key, or empty when the page has none.</param>
/// <param name="BackName">What that background is called.</param>
/// <param name="Field">
///   The background's source: one function, from a point on the page to a number. Empty when there
///   is no background, or when the card names one this version has never heard of.
/// </param>
/// <param name="Json">The document, exactly as it travels.</param>
/// <param name="Bytes">What that document weighs, without its pictures.</param>
/// <param name="Pictures">How many pictures the page carries, which is where the size really goes.</param>
public sealed record CardSource(
    IReadOnlyList<CardPiece> Pieces,
    string Look,
    string LookName,
    IReadOnlyList<CardToken> Tokens,
    string Back,
    string BackName,
    string Field,
    string Json,
    int Bytes,
    int Pictures)
{
    /// <summary>Read a card the way somebody who wants to make one would.</summary>
    public static CardSource Of(CardDocument? card)
    {
        card ??= new CardDocument();

        var look = CardLook.FromCard(card);
        var back = CardShader.FromCard(card);
        var json = card.ToJson();

        return new CardSource(
            Pieces: [.. Read(card)],
            Look: look.Key,
            LookName: look.Name,
            Tokens: [.. Values(look)],
            Back: back.Key,
            BackName: back.Name,
            Field: back.Field ?? "",
            Json: json,
            Bytes: Encoding.UTF8.GetByteCount(json),
            Pictures: card.Blocks?.Count(b => b.Kind == CardBlock.Image) ?? 0);
    }

    /// <summary>
    /// The blocks, as a person would list them.
    /// </summary>
    /// <remarks>
    /// The theme blocks are left in rather than tidied away. They are how the page carries its look
    /// and its background, and somebody reading this to find out how a page works should see that the
    /// design is stored in the document like everything else, not applied to it from outside.
    /// </remarks>
    private static IEnumerable<CardPiece> Read(CardDocument card)
    {
        foreach (var block in card.Blocks ?? [])
            yield return new CardPiece(
                block.Kind,
                OwnCard.Label(block.Kind),
                Said(block));
    }

    /// <summary>A number as a stylesheet writes it, whatever the phone is set to.</summary>
    private static string Said(IFormattable number) => number.ToString(null, CultureInfo.InvariantCulture);

    private static string Said(CardBlock block) => block.Kind switch
    {
        CardBlock.Theme => block.Value ?? "",
        CardBlock.Image => block.Value is { Length: > 0 } caption ? caption : "a picture",
        CardBlock.Rule => block.Value is { Length: > 0 } mark ? mark : "a hairline",
        _ when block.Items is { Count: > 0 } items => string.Join(" · ", items.Take(3)),
        _ => block.Value ?? "",
    };

    /// <summary>
    /// A look, as the values it actually is.
    /// </summary>
    /// <remarks>
    /// This is the moment worth having. "Editorial" is a name, and a name teaches nothing; thirteen
    /// numbers and colours is a design system, and somebody who sees that the whole page comes out of
    /// thirteen values has learned what design tokens are without the phrase being used.
    /// </remarks>
    private static IEnumerable<CardToken> Values(CardLook look)
    {
        yield return new("paper", look.Paper, "the ground the page is printed on");
        yield return new("ink", look.Ink, "everything written on it");
        yield return new("accent", look.Accent, "the one colour used sparingly");
        yield return new("paper at night", look.PaperDark,
            look.Fixed ? "unused — this look keeps its ground whatever the phone is set to" : "the ground in the dark");
        yield return new("ink at night", look.InkDark,
            look.Fixed ? "unused — see above" : "the writing in the dark");
        yield return new("display", look.Display, "the face headings are set in");
        yield return new("body", look.Body, "the face everything else is set in");
        // Written the way the page writes them.
        //
        // These are values from a stylesheet, not numbers in a sentence, and this phone is set to a
        // locale that puts a comma in a decimal: the panel said the leading was "1,74" while the page
        // it claims to be showing said 1.74. The whole promise here is "this is the real thing" —
        // somebody who copies what they read and gets invalid CSS has been told something untrue.
        yield return new("weight", Said(look.BodyWeight), "how heavy running text is — 300 is light, 400 is normal");
        yield return new("size", Said(look.BodySize) + "px", "running text, bigger than an app would use — this is a page");
        yield return new("leading", Said(look.Leading), "the gap between lines, as a multiple of the size");
        yield return new("measure", Said(look.Measure) + "rem", "how wide a line may get — about sixty-five characters");
    }
}

/// <summary>One piece of a page.</summary>
/// <param name="Kind">What the document calls it.</param>
/// <param name="Name">What a person calls it.</param>
/// <param name="Said">What it says, shortened.</param>
public readonly record struct CardPiece(string Kind, string Name, string Said);

/// <summary>One value a look is made of.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Value">What it is set to.</param>
/// <param name="Says">What it does, in a sentence.</param>
public readonly record struct CardToken(string Name, string Value, string Says);
