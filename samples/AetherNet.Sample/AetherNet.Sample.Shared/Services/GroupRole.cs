// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Which of two phones creates the Wi-Fi Direct group, and which one joins it.
///
/// <para>
/// Somebody has to host and somebody has to join, and the two phones have to agree without asking each
/// other — there is no channel to negotiate on yet, which is the entire problem being solved. Both
/// ends calling connect() at the same moment is what puts Android's "Invitation to connect" dialog in
/// front of a person who asked for nothing, and it is why the group used to form only when somebody
/// happened to be looking at the screen.
/// </para>
///
/// <para>
/// So the answer is computed rather than negotiated: order the two AetherTags and the lower one hosts.
/// Both sides hold both tags by the time this is asked, both run the same comparison, and both get the
/// same answer — one creates, one joins, nobody is asked to confirm anything.
/// </para>
/// </summary>
public static class GroupRole
{
    /// <summary>
    /// Does this phone host the group it would share with <paramref name="theirTag"/>?
    /// </summary>
    /// <remarks>
    /// Ordinal, so the two phones compare bytes rather than anything a locale could disagree about.
    /// A tag against itself hosts nothing — a phone does not form a group with itself, and returning
    /// true there would have it sit hosting an empty group forever.
    /// </remarks>
    public static bool HostsTheGroup(string? myTag, string? theirTag)
    {
        if (string.IsNullOrEmpty(myTag) || string.IsNullOrEmpty(theirTag)) return false;
        return string.CompareOrdinal(myTag, theirTag) < 0;
    }
}
