// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What a stranger reads when this phone offers them something.
///
/// <para>
/// <b>Why this exists at all.</b> The objection that shaped Touch My Blood was that a raw IP address
/// looks like a scam to somebody being handed an app by a friend. We removed the address — and then
/// put <c>KXJB7-MN2P4</c> in its place, on the network name and on the card. A person cannot tell a
/// random address from a random code. We solved the wrong half.
/// </para>
///
/// <para>
/// An AetherTag is this device's address and it belongs on the wire. It does not belong in the one
/// place a human being is deciding whether to trust what is in front of them. That place needs a
/// name, because "Aether · Thabang" is a sentence somebody can act on and a hex string is not.
/// </para>
///
/// <para>
/// Stored on the device and nowhere else. It is not an account, it is not checked against anything,
/// and nobody is asked to prove it — the proof is that the person saying it is standing next to you.
/// </para>
/// </summary>
public static class MyName
{
    /// <summary>Where it lives in this device's own settings.</summary>
    public const string Key = "my_name";

    /// <summary>
    /// The longest name we will carry.
    /// </summary>
    /// <remarks>
    /// A Wi-Fi network name is 32 characters and Android spends seven of them on a prefix it forces.
    /// The word "Aether" and a space take another seven, so what is left for a person is eighteen.
    /// Names are cut here rather than there, so the same name appears on the network, on the card and
    /// on the screen instead of three different truncations of it.
    /// </remarks>
    public const int Longest = 18;

    /// <summary>
    /// Tidy a name into something that survives a Wi-Fi picker and a web page.
    /// </summary>
    /// <remarks>
    /// Letters, digits, spaces, hyphens and apostrophes — enough for real names, including O'Brien and
    /// Van Der Merwe, without admitting anything that renders as a box on somebody else's handset. A
    /// name that arrives as boxes is worse than no name, because a box is not a person.
    /// </remarks>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var clean = new StringBuilder(Longest);
        var lastWasSpace = true;

        foreach (var c in raw.Trim())
        {
            if (clean.Length >= Longest) break;

            if (char.IsLetterOrDigit(c) || c is '-' or '\'')
            {
                clean.Append(c);
                lastWasSpace = false;
            }
            // Any whitespace separates, not just a space. A tab dropped outright would run two names
            // together — "Sipho\tM" becoming "SiphoM" — which is a worse answer than either keeping
            // it or losing the second word.
            else if (char.IsWhiteSpace(c) && !lastWasSpace)
            {
                clean.Append(' ');
                lastWasSpace = true;
            }
        }

        return clean.ToString().Trim();
    }

    /// <summary>Whether somebody has told us who they are.</summary>
    public static bool IsSet(string? name) => Clean(name).Length > 0;

    /// <summary>
    /// What to show where a person is being asked to trust this phone.
    /// </summary>
    /// <param name="name">The name, if one has been given.</param>
    /// <param name="aetherTag">This device's address, as a last resort.</param>
    /// <remarks>
    /// Falls back to the tag rather than to nothing: an unreadable code is bad, and an unsigned
    /// offer is worse. But the fallback is a failure state, not a design — anywhere it appears, we
    /// have asked a stranger to trust a string of characters.
    /// </remarks>
    public static string OrTag(string? name, string aetherTag) =>
        Clean(name) is { Length: > 0 } given ? given : aetherTag;
}
