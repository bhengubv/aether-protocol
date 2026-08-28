// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// The catalogue of breaks — what sits between two parts of a page.
///
/// <para>
/// A hairline is a break that says nothing. The pages this library is measured against use a brush
/// mark, a scatter of dots, a swell — something with a hand in it — and that one element does a
/// surprising amount of the work: it is the difference between a page that was set and a page that
/// was divided into sections.
/// </para>
///
/// <para>
/// Drawn, never fetched. Each entry is a few hundred bytes of SVG in the page's own accent, so it
/// costs about as much as the word "break" and works on a phone with no internet, no assets and no
/// network of any kind.
/// </para>
///
/// <para>
/// Same shape as the other catalogues: adding one is adding a record, and the editor, the renderer
/// and the picker all take it without further change.
/// </para>
/// </summary>
/// <param name="Key">What a card names to ask for this break.</param>
/// <param name="Name">What a person choosing it reads.</param>
/// <param name="Draw">
///   The mark, as SVG for a <c>0 0 240 24</c> box. Use <c>currentColor</c> so it takes the page's
///   accent — an ornament that named its own colour would be the one that looked wrong on four looks
///   out of five.
/// </param>
public sealed record CardOrnament(string Key, string Name, string Draw)
{
    /// <summary>What a break gets when it asks for nothing.</summary>
    public const string DefaultKey = "line";

    /// <summary>Every break this library ships.</summary>
    public static readonly CardOrnament[] All =
    [
        new("line", "Hairline",
            "<rect x='0' y='11.5' width='240' height='1' fill='currentColor' opacity='.28'/>"),

        new("brush", "Brush",
            "<path d='M6 15c26-7 46 4 72-1s44-9 70-4 52 8 86 2' stroke='currentColor' " +
            "stroke-width='7' stroke-linecap='round' fill='none' opacity='.55'/>" +
            "<path d='M18 18c30-5 48 2 74-2s48-6 74-2 44 6 60 3' stroke='currentColor' " +
            "stroke-width='3' stroke-linecap='round' fill='none' opacity='.32'/>"),

        new("dots", "Dots",
            "<g fill='currentColor' opacity='.45'>" +
            "<circle cx='96' cy='12' r='2.2'/><circle cx='112' cy='12' r='2.2'/>" +
            "<circle cx='128' cy='12' r='2.2'/><circle cx='144' cy='12' r='2.2'/></g>"),

        new("swell", "Swell",
            "<path d='M0 12q30-9 60 0t60 0 60 0 60 0' stroke='currentColor' stroke-width='1.6' " +
            "fill='none' opacity='.42'/>"),

        new("rule", "Rule and mark",
            "<rect x='0' y='11.5' width='104' height='1' fill='currentColor' opacity='.24'/>" +
            "<rect x='136' y='11.5' width='104' height='1' fill='currentColor' opacity='.24'/>" +
            "<circle cx='120' cy='12' r='3' fill='currentColor' opacity='.5'/>"),

        new("scatter", "Scatter",
            "<g fill='currentColor' opacity='.4'>" +
            "<circle cx='72' cy='9' r='1.6'/><circle cx='88' cy='16' r='2.4'/>" +
            "<circle cx='108' cy='7' r='1.2'/><circle cx='120' cy='13' r='3'/>" +
            "<circle cx='138' cy='18' r='1.8'/><circle cx='152' cy='10' r='1.3'/>" +
            "<circle cx='168' cy='15' r='2'/></g>"),
    ];

    /// <summary>The break with this key, or the plain one.</summary>
    /// <remarks>
    /// An unknown key is a newer author, not a broken card — so it comes out as a hairline rather
    /// than as nothing, and the page still has its pause in the right place.
    /// </remarks>
    public static CardOrnament Of(string? key)
    {
        var wanted = key?.Trim().ToLowerInvariant();
        return All.FirstOrDefault(o => o.Key == wanted) ?? All.First(o => o.Key == DefaultKey);
    }

    /// <summary>Whether this is a break we ship.</summary>
    public static bool IsOrnament(string? key) =>
        key is not null && All.Any(o => o.Key == key.Trim().ToLowerInvariant());

    /// <summary>The mark, as a complete SVG element ready to place in a page.</summary>
    public string Svg() =>
        "<svg class=\"orn\" viewBox=\"0 0 240 24\" preserveAspectRatio=\"none\" " +
        "xmlns=\"http://www.w3.org/2000/svg\" aria-hidden=\"true\">" + Draw + "</svg>";
}
