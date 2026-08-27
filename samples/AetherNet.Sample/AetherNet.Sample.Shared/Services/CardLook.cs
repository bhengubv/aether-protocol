// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// A complete look for somebody's card — type, colour, decoration and layout together.
///
/// <para>
/// <b>Why a look rather than a palette.</b> Handing somebody a colour picker does not produce a page
/// that looks designed; it produces a page that looks like somebody picked a colour. What separates a
/// page a designer was paid for from one that was not is mostly typography and restraint — a real
/// typeface at a considered size, a measure that suits it, space used deliberately. Nobody without
/// training reaches that from a hex field, and asking them to is how personal pages ended up looking
/// the way personal pages used to look.
/// </para>
///
/// <para>
/// So the choice offered is not "what colour" but "which of these". Each look is a finished design
/// carrying its own typeface, scale and ornament, and the person supplies only what is theirs: their
/// name, their words, their photographs. They choose a design rather than making one, which is the
/// only path by which somebody with no training ends up with a page that holds up.
/// </para>
///
/// <para>
/// <b>Fonts are the load-bearing part and they must travel.</b> The reader has joined a phone-to-phone
/// network with no way out to the internet, so a web font that is linked never arrives and the page
/// falls back to whatever the handset has. Instrument Serif is fifteen kilobytes — the difference
/// between a card that looks made and one that looks typed, for less than a small photograph.
/// </para>
/// </summary>
/// <param name="Accent">
/// The look's colour as a plain hex value. A look names a palette, and a palette is a stylesheet the
/// handout renderer owns; the mesh-web browser draws cards itself and reads a single accent instead,
/// and the generated masthead is a third drawing again. Declaring the colour once is what stops a page
/// coming out sepia with a green picture above it.
/// </param>
public sealed record CardLook(
    string Key,
    string Name,
    string Blurb,
    string Display,
    string Body,
    string Scheme,
    string Accent)
{
    /// <summary>Looks shipped with the app, in the order the editor offers them.</summary>
    /// <remarks>
    /// Deliberately few. A long list is a decision a person cannot make well, and every extra look is
    /// one more thing to keep looking good on a handset. Five is enough to feel like a choice and
    /// small enough that all five can be genuinely finished.
    /// </remarks>
    public static readonly CardLook[] All =
    [
        new("plain", "Plain",
            "Quiet and legible. Nothing to distract from what you wrote.",
            Display: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Body: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Scheme: "mono", Accent: "#1c1c1a"),

        new("terminal", "Terminal",
            "Monospace throughout. Precise, technical, unfussy.",
            Display: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
            Body: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
            Scheme: "mono", Accent: "#1c1c1a"),

        new("editorial", "Editorial",
            "A serif headline over generous text. Reads like a page, not a profile.",
            Display: "'Instrument Serif', Georgia, 'Times New Roman', serif",
            Body: "'Newsreader', Georgia, 'Times New Roman', serif",
            Scheme: "sepia", Accent: "#7a4a1e"),

        new("studio", "Studio",
            "Big type, tight spacing, plenty of air. For work you want looked at.",
            Display: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Body: "'Newsreader', Georgia, 'Times New Roman', serif",
            Scheme: "moss", Accent: "#2c5a33"),

        new("night", "Night",
            "Dark, low-contrast, unhurried. Good for photographs.",
            Display: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Body: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Scheme: "azure", Accent: "#1a4f7a"),
    ];

    /// <summary>What a card gets when it asks for nothing.</summary>
    public const string DefaultKey = "plain";

    /// <summary>The look with this key, or the default.</summary>
    /// <remarks>
    /// A card asking for a look this version does not have is drawn in the default rather than
    /// refused. An unknown key is a newer author, not a broken card — the same rule the block model
    /// already follows.
    /// </remarks>
    public static CardLook Of(string? key)
    {
        var wanted = key?.Trim().ToLowerInvariant();
        return All.FirstOrDefault(l => l.Key == wanted) ?? All.First(l => l.Key == DefaultKey);
    }

    /// <summary>Whether this is a look we ship.</summary>
    public static bool IsLook(string? key) =>
        key is not null && All.Any(l => l.Key == key.Trim().ToLowerInvariant());

    /// <summary>
    /// The typefaces this look needs carried with the page, by family name.
    /// </summary>
    /// <remarks>
    /// Only families that are not on a phone already. A look built from system faces asks for nothing
    /// and costs nothing, which is why the default is one of those — a first card should be instant
    /// even before anybody has chosen anything.
    /// </remarks>
    public IEnumerable<string> Faces()
    {
        foreach (var family in new[] { Display, Body })
        {
            var quoted = family.Split(',')[0].Trim();
            if (quoted.StartsWith('\'') && quoted.EndsWith('\''))
                yield return quoted.Trim('\'');
        }
    }

    /// <summary>The look a card asked for, read from its theme block.</summary>
    public static CardLook FromCard(CardDocument? card)
    {
        var asked = card?.Blocks?
            .FirstOrDefault(b => b.Kind == CardBlock.Theme && IsLook(b.Value))?
            .Value;

        return Of(asked);
    }
}
