// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// A complete look for a card — ground, ink, typefaces, weight, leading and measure together.
///
/// <para>
/// <b>Why a look rather than a palette.</b> Handing somebody a colour picker does not produce a page
/// that looks designed; it produces a page that looks like somebody picked a colour. What separates a
/// page a designer was paid for from one that was not is mostly typography and restraint — a real
/// typeface at a considered size, a weight chosen rather than defaulted, a measure that suits it,
/// space used deliberately. Nobody without training reaches that from a hex field.
/// </para>
///
/// <para>
/// So the choice offered is not "what colour" but "which of these", and every number that makes a
/// design work is carried here rather than hidden in the renderer. That is also what makes this a
/// thing to build on: <b>adding a look is adding one record</b>, and the renderer needs no changes to
/// draw it.
/// </para>
///
/// <para>
/// <b>The numbers are measured, not invented.</b> Editorial's are taken off a page that already
/// works: warm paper, a warm near-black rather than a grey, every secondary tone the same ink at a
/// lower opacity, a light serif at seventeen pixels with leading near 1.75, and a measure around
/// thirty-two rems. The light weight and the air around it are the texture — set the same page in the
/// regular weight at 1.5 and it stops being editorial and becomes a document.
/// </para>
///
/// <para>
/// <b>Fonts are load-bearing and they must travel.</b> A reader on a phone-to-phone network has no
/// way out to the internet, so a linked web font never arrives and the page falls back to whatever
/// the handset has. Instrument Serif is fifteen kilobytes — the difference between a card that looks
/// made and one that looks typed, for less than a small photograph.
/// </para>
/// </summary>
/// <param name="Key">What a card names to ask for this look.</param>
/// <param name="Name">What a person choosing it reads.</param>
/// <param name="Blurb">What it is for, in one line.</param>
/// <param name="Display">The typeface stack for titles and marks.</param>
/// <param name="Body">The typeface stack for everything else.</param>
/// <param name="Paper">The ground, in light. Warm or cool, but chosen — never a default white.</param>
/// <param name="Ink">The text colour, in light. Every dimmer tone is this at a lower alpha.</param>
/// <param name="PaperDark">The ground when the reader's phone is dark.</param>
/// <param name="InkDark">The text colour when the reader's phone is dark.</param>
/// <param name="Accent">
///   One colour, used for the masthead, the rule under the title, and anything a reader may act on.
///   A plain hex value, because it is handed to a shader and written into styles.
/// </param>
/// <param name="BodyWeight">
///   The weight running text is set in. The one number most likely to be left at its default and most
///   responsible for whether a page reads as designed.
/// </param>
/// <param name="BodySize">Running text size in pixels. Bigger than a UI would use — this is a page.</param>
/// <param name="Leading">Line height as a multiple. Around 1.75 for a light serif; less for monospace.</param>
/// <param name="Measure">
///   How wide running text may get, in rems. Roughly sixty to seventy characters, which is where
///   prose stops being work — a full-width paragraph on a wide screen is unreadable however good the
///   typeface is.
/// </param>
public sealed record CardLook(
    string Key,
    string Name,
    string Blurb,
    string Display,
    string Body,
    string Paper,
    string Ink,
    string PaperDark,
    string InkDark,
    string Accent,
    int BodyWeight,
    double BodySize,
    double Leading,
    double Measure)
{
    /// <summary>Looks shipped with the library, in the order an editor offers them.</summary>
    /// <remarks>
    /// Deliberately few. A long list is a decision a person cannot make well, and every extra look is
    /// one more thing to keep good on a handset. Five is enough to feel like a choice and few enough
    /// that all five can be genuinely finished.
    /// </remarks>
    public static readonly CardLook[] All =
    [
        new("plain", "Plain",
            "Quiet and legible. Nothing to distract from what you wrote.",
            Display: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Body: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Paper: "#F4F4F2", Ink: "#17181A",
            PaperDark: "#0C0D0F", InkDark: "#F1F2F4",
            Accent: "#1C1C1A",
            BodyWeight: 400, BodySize: 16.5, Leading: 1.62, Measure: 34),

        new("terminal", "Terminal",
            "Monospace throughout. Precise, technical, unfussy.",
            Display: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
            Body: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
            Paper: "#EDEDEA", Ink: "#14150F",
            PaperDark: "#08090A", InkDark: "#DFE3DA",
            Accent: "#1C1C1A",
            // Monospace is wide and even, so it wants a tighter measure and less leading than a
            // serif does — the same numbers would look airy to the point of loose.
            BodyWeight: 400, BodySize: 15, Leading: 1.55, Measure: 30),

        new("editorial", "Editorial",
            "A serif headline over generous text. Reads like a page, not a profile.",
            Display: "'Instrument Serif', 'New York', Georgia, 'Times New Roman', serif",
            Body: "'Newsreader', 'New York', Georgia, 'Times New Roman', serif",
            // Measured, not invented. Warm paper and a warm near-black — a neutral grey on this
            // ground reads as printing that has gone slightly wrong.
            Paper: "#ECE7DC", Ink: "#2B2721",
            PaperDark: "#141210", InkDark: "#EFE9DE",
            Accent: "#7A4A1E",
            // The light weight and the air are the whole texture. At 400 and 1.5 the same page is a
            // document rather than an editorial one.
            BodyWeight: 300, BodySize: 17, Leading: 1.74, Measure: 33) { Fixed = true },

        new("studio", "Studio",
            "Big type, tight spacing, plenty of air. For work you want looked at.",
            Display: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Body: "'Newsreader', 'New York', Georgia, 'Times New Roman', serif",
            Paper: "#EFF1EC", Ink: "#12180F",
            PaperDark: "#0B0E09", InkDark: "#EAF0E6",
            Accent: "#2C5A33",
            BodyWeight: 300, BodySize: 17.5, Leading: 1.7, Measure: 32),

        new("night", "Night",
            "Dark, low-contrast, unhurried. Good for photographs.",
            Display: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            Body: "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
            // The one look that is dark by intent rather than by the reader's setting, so both
            // grounds are dark and the light one is merely a shade up.
            Paper: "#15181C", Ink: "#E8EDF3",
            PaperDark: "#0A0C0F", InkDark: "#E8EDF3",
            Accent: "#6FB2E8",
            BodyWeight: 400, BodySize: 16.5, Leading: 1.68, Measure: 33) { Fixed = true },
    ];

    /// <summary>What a card gets when it asks for nothing.</summary>
    public const string DefaultKey = "plain";

    /// <summary>The look with this key, or the default.</summary>
    /// <remarks>
    /// A card asking for a look this version does not have is drawn in the default rather than
    /// refused. An unknown key is a newer author, not a broken card — the same rule the block model
    /// follows.
    /// </remarks>
    public static CardLook Of(string? key)
    {
        var wanted = key?.Trim().ToLowerInvariant();
        return All.FirstOrDefault(l => l.Key == wanted) ?? All.First(l => l.Key == DefaultKey);
    }

    /// <summary>Whether this is a look we ship.</summary>
    public static bool IsLook(string? key) =>
        key is not null && All.Any(l => l.Key == key.Trim().ToLowerInvariant());

    /// <summary>The look a card asked for, read from its theme block.</summary>
    public static CardLook FromCard(CardDocument? card)
    {
        var asked = card?.Blocks?
            .FirstOrDefault(b => b.Kind == CardBlock.Theme && IsLook(b.Value))?
            .Value;

        return Of(asked);
    }

    /// <summary>
    /// The typefaces this look needs carried with the page, by family name.
    /// </summary>
    /// <remarks>
    /// Only families a phone will not already have. A look built from system faces asks for nothing
    /// and costs nothing, which is why the default is one of those — a first card should be instant
    /// before anybody has chosen anything.
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

    /// <summary>
    /// This look as CSS custom properties, for both the reader's grounds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every dimmer tone is the ink at a lower alpha rather than a separate grey. That is the single
    /// habit that makes a palette hold together: a secondary colour mixed independently drifts away
    /// from the ground it sits on, and on a warm paper it reads as printing gone wrong.
    /// </para>
    /// <para>
    /// Three states, because the reader has three. An explicit choice stamps the root; the default
    /// setting stamps nothing and only <c>prefers-color-scheme</c> separates light from dark. A colour
    /// defined only inside a media query is the classic unreadable page.
    /// </para>
    /// </remarks>
    public string Tokens()
    {
        var light = Palette(Paper, Ink);
        var dark = Palette(PaperDark, InkDark);

        // A look that is a material rather than a colour scheme keeps its ground.
        //
        // Paper is paper. Following the reader's phone into dark mode turned the one look built to
        // feel like a printed page into another dark app screen — which is most of what made it
        // recognisable, given away to a setting its author never touched. A look that says it is a
        // surface says so here; the rest still follow the reader.
        if (Fixed)
            return ":root{" + light + Type() + "}";

        return
            ":root{" + light + Type() + "}" +
            "@media(prefers-color-scheme:dark){:root:not([data-look-mode=\"light\"]){" + dark + "}}" +
            ":root[data-look-mode=\"dark\"]{" + dark + "}";
    }

    /// <summary>
    /// Whether this look keeps its ground whatever the reader's phone is set to.
    /// </summary>
    /// <remarks>
    /// True for the looks that are a material — paper, or a night that is dark by intent rather than
    /// by setting. False for the ones that are simply a page, which should follow the reader.
    /// </remarks>
    public bool Fixed { get; init; }

    private string Palette(string paper, string ink) =>
        $"--paper:{paper};--ink:{ink};--accent:{Accent};" +
        $"--ink-2:color-mix(in srgb,{ink} 58%,transparent);" +
        $"--ink-3:color-mix(in srgb,{ink} 36%,transparent);" +
        $"--rule:color-mix(in srgb,{ink} 14%,transparent);" +
        $"--tint:color-mix(in srgb,{ink} 5%,transparent);";

    private string Type() =>
        $"--display:{Display};--body:{Body};" +
        $"--weight:{BodyWeight};" +
        $"--size:{BodySize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px;" +
        $"--leading:{Leading.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
        $"--measure:{Measure.ToString(System.Globalization.CultureInfo.InvariantCulture)}rem;";
}
