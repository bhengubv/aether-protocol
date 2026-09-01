// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Gives every AetherTag a stable colour, so a person looks the same everywhere in the app — their
/// avatar in the chat list, the header of your conversation, and (where it matters most) who said
/// what in a group, at a glance, without reading the tag.
///
/// The colour is derived from the tag itself, so it needs no storage, no coordination, and both
/// phones independently agree on it: the person who is teal on your phone is teal on theirs.
///
/// Every colour is a shade of the single brand blue — the blue mixed into black, never a second hue.
/// The palette is #2196F3 / black / white, and these are the first two of those and nothing else; a
/// teal or a slate would be a fourth colour smuggled in under "brand family". Darkness alone tells
/// several people apart on a small screen, and every shade stays dark enough for the white initial to
/// stay readable.
/// </summary>
public static class TagPalette
{
    /// <summary>The one brand blue, in bytes.</summary>
    private static readonly (int R, int G, int B) Brand = (0x21, 0x96, 0xF3);

    /// <summary>Eight shades of that blue, base through progressively darker.</summary>
    private static readonly string[] Colours = Shades(8);

    /// <summary>
    /// The brand blue mixed toward black in even steps — one hue, only the lightness moving.
    /// </summary>
    /// <remarks>
    /// Scaling all three channels by the same factor keeps the exact hue and only drops the lightness,
    /// so no step is ever a different colour — it is the blue with more black in it. This is what the
    /// three-colour rule allows and the old ten-hue list (cerulean, indigo, slate, teal…) did not.
    /// </remarks>
    private static string[] Shades(int count)
    {
        var shades = new string[count];

        for (var i = 0; i < count; i++)
        {
            var keep = 1.0 - i * 0.075;   // 1.00 (base blue) down to ~0.48 (dark blue)
            var r = (int)Math.Round(Brand.R * keep);
            var g = (int)Math.Round(Brand.G * keep);
            var b = (int)Math.Round(Brand.B * keep);
            shades[i] = $"#{r:X2}{g:X2}{b:X2}";
        }

        return shades;
    }

    /// <summary>The colour for a tag. Same tag, same colour, on every device, forever.</summary>
    public static string For(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return Colours[0];

        // A plain deterministic hash — it must agree across devices and runs, so no randomness and
        // nothing machine-specific (string.GetHashCode is randomised per process and would not).
        var hash = 0;
        foreach (var c in tag) hash = unchecked(hash * 31 + c);
        return Colours[Math.Abs(hash % Colours.Length)];
    }

    /// <summary>The first character of a tag, for the avatar.</summary>
    public static string Initial(string? tag) =>
        string.IsNullOrEmpty(tag) ? "?" : tag[..1].ToUpperInvariant();
}
