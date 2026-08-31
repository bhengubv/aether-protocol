// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// The typefaces a card is allowed to name.
///
/// <para>
/// <b>A closed list, and it has to be.</b> A reader on a phone-to-phone network has no way out to the
/// internet, so a card naming a font it does not carry gets whatever the handset happens to have —
/// which on a cheap Android is not the page the author saw. Only faces that travel in the page are
/// offered, plus the two the operating system is certain to have.
/// </para>
///
/// <para>
/// <b>Why a name and not a stack.</b> A card says <c>display = serif</c>; what that expands into is
/// ours. An author cannot write a font stack into a card, which is the same rule as everywhere else
/// here: the author turns dials, the renderer holds the machine. A stack is a place to hide a URL.
/// </para>
/// </summary>
public static class Typeface
{
    /// <summary>What an author may ask for, and what each one becomes.</summary>
    /// <remarks>
    /// Instrument Serif and Newsreader ship in the page at about fifteen kilobytes each — less than a
    /// small photograph, and the difference between a card that looks made and one that looks typed.
    /// System and Mono carry nothing and are always there.
    /// </remarks>
    private static readonly (string Name, string Blurb, string Stack)[] Offered =
    [
        ("serif", "Instrument Serif — a display serif, high contrast, for titles",
            "'Instrument Serif', 'New York', Georgia, 'Times New Roman', serif"),

        ("reader", "Newsreader — a text serif that holds up at length",
            "'Newsreader', 'New York', Georgia, 'Times New Roman', serif"),

        ("system", "The handset's own sans — invisible, and free to carry",
            "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif"),

        ("mono", "Monospace — precise, technical, every character the same width",
            "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace"),
    ];

    /// <summary>The names an editor offers, with what each is for.</summary>
    public static IEnumerable<(string Name, string Blurb)> All =>
        Offered.Select(f => (f.Name, f.Blurb));

    /// <summary>Whether a card may name this face.</summary>
    public static bool IsOffered(string? name) =>
        name is { Length: > 0 } && Offered.Any(f => f.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>What the name expands into. Unknown names get the system face rather than nothing.</summary>
    public static string Stack(string? name) =>
        Offered.FirstOrDefault(f => f.Name.Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            .Stack ?? Offered[2].Stack;
}
