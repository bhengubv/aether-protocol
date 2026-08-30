// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// The small marks a row of links can be drawn as.
///
/// <para>
/// <b>Why a card needs these.</b> Every page that points at the rest of somebody's life does it with
/// a row of little marks — write to me, watch this, buy that. Spelled out as words they take a
/// paragraph and read as a menu; as marks they take one line and read as a signature. A card that
/// could only do words was a card that could not do the thing every page does at its foot.
/// </para>
///
/// <para>
/// <b>Why they are these marks and not the famous ones.</b> A logo is somebody's trademark, and this
/// is an MIT repository that other people will ship. So these say what the link is <i>for</i> — a
/// letter, a shop, a map, a film — rather than who it goes to, which is also the more honest thing
/// for a reader: the mark tells them what will happen, and the address tells them where.
/// </para>
///
/// <para>
/// <b>Adding one is adding a record.</b> Same as <see cref="CardShader"/> and
/// <see cref="CardOrnament"/>: the geometry lives here, the renderer knows nothing about any of them
/// by name, and a card naming an icon this version has never heard of simply gets its words.
/// </para>
/// </summary>
/// <param name="Key">What a card stores.</param>
/// <param name="Name">What the editor calls it, and what a reader's screen reader says.</param>
/// <param name="Draw">
///   The inner markup, drawn inside a 24×24 box with <c>currentColor</c> strokes and no fill — so an
///   icon takes the look's ink without any icon knowing what a look is.
/// </param>
public readonly record struct CardIcon(string Key, string Name, string Draw)
{
    /// <summary>Every mark a link may wear.</summary>
    public static readonly CardIcon[] All =
    [
        new("mail", "Email",
            "<rect x='3' y='5' width='18' height='14' rx='2'/><path d='m3.5 7 8.5 6 8.5-6'/>"),

        new("phone", "Phone",
            "<path d='M6.6 3H4.5A1.5 1.5 0 0 0 3 4.6C3 12.5 11.5 21 19.4 21a1.5 1.5 0 0 0 1.6-1.5v-2.1" +
            "l-4-1.4-2 2a13.4 13.4 0 0 1-5-5l2-2z'/>"),

        new("chat", "Message", "<path d='M4 5h16v11H9l-5 4z'/>"),

        new("person", "Profile",
            "<circle cx='12' cy='8' r='3.5'/><path d='M4.5 20a7.5 7.5 0 0 1 15 0'/>"),

        new("photo", "Photographs",
            "<rect x='3' y='4' width='18' height='16' rx='2'/><circle cx='8.5' cy='9.5' r='1.5'/>" +
            "<path d='m4 17 5-5 4 4 3-3 4 4'/>"),

        new("video", "Video",
            "<rect x='3' y='5' width='18' height='14' rx='3'/><path d='m10 9 6 3-6 3z'/>"),

        new("music", "Music",
            "<path d='M9 18V6l10-2v12'/><circle cx='6.5' cy='18' r='2.5'/><circle cx='16.5' cy='16' r='2.5'/>"),

        new("code", "Code", "<path d='m8 8-4 4 4 4M16 8l4 4-4 4M13.5 5l-3 14'/>"),

        new("doc", "Writing",
            "<path d='M14 3H7a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V7z'/>" +
            "<path d='M14 3v4h4M9 13h6M9 17h4'/>"),

        new("shop", "Shop",
            "<path d='M5 8h14l-1 12H6z'/><path d='M9 8V6a3 3 0 0 1 6 0v2'/>"),

        new("map", "Where to find me",
            "<path d='M12 21s7-5.6 7-11a7 7 0 1 0-14 0c0 5.4 7 11 7 11z'/><circle cx='12' cy='10' r='2.5'/>"),

        new("calendar", "Book a time",
            "<rect x='3' y='5' width='18' height='16' rx='2'/><path d='M3 10h18M8 3v4M16 3v4'/>"),

        new("star", "Reviews",
            "<path d='m12 3 2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 17.3 6.4 20.2l1.1-6.2L3 9.6l6.2-.9z'/>"),

        new("globe", "Website",
            "<circle cx='12' cy='12' r='9'/>" +
            "<path d='M3 12h18M12 3c2.5 2.7 3.8 5.8 3.8 9S14.5 18.3 12 21c-2.5-2.7-3.8-5.8-3.8-9S9.5 5.7 12 3z'/>"),
    ];

    /// <summary>Whether a card has named a mark this version knows.</summary>
    public static bool IsIcon(string? key) =>
        key is { Length: > 0 } && All.Any(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The mark a card named, or nothing.</summary>
    public static CardIcon? Of(string? key) =>
        All.FirstOrDefault(i => i.Key.Equals(key ?? "", StringComparison.OrdinalIgnoreCase)) is { Key.Length: > 0 } found
            ? found
            : null;

    /// <summary>
    /// The mark, as an inline drawing.
    /// </summary>
    /// <remarks>
    /// No <c>xmlns</c>, deliberately — inline SVG in an HTML document does not need one, and leaving
    /// it out means an exported card contains no <c>http</c> anywhere at all. Somebody checking that a
    /// page reaches for nothing should not have to reason about whether a namespace counts.
    /// </remarks>
    public string Svg() =>
        "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.6' " +
        "stroke-linecap='round' stroke-linejoin='round' aria-hidden='true'>" + Draw + "</svg>";
}
