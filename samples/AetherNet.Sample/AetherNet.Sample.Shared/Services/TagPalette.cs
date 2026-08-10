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
/// Stays inside the brand family — blues through slate, never orange — while keeping enough hue and
/// lightness separation that several people in one conversation remain tellable apart on a small
/// screen, in both light and dark.
/// </summary>
public static class TagPalette
{
    private static readonly string[] Colours =
    {
        "#2196F3", // brand blue
        "#1565C0", // deep blue
        "#4aa8ff", // sky
        "#0D47A1", // navy
        "#3F51B5", // indigo
        "#2c3e50", // slate
        "#00838F", // teal
        "#5C6BC0", // periwinkle
        "#01579B", // ocean
        "#546E7A", // blue-grey
    };

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

    /// <summary>A faint wash of the person's colour, for tinting the surface behind their words.</summary>
    public static string Wash(string? tag) => For(tag) + "22";   // ~13% alpha

    /// <summary>The first character of a tag, for the avatar.</summary>
    public static string Initial(string? tag) =>
        string.IsNullOrEmpty(tag) ? "?" : tag[..1].ToUpperInvariant();
}
