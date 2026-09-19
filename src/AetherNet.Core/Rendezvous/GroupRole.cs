// SPDX-License-Identifier: MIT

namespace AetherNet.Rendezvous;

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

    /// <summary>
    /// Does this phone host once the radio's <b>ability</b> to host has a say, not just the tags?
    /// </summary>
    /// <param name="proposedHost">
    ///   What the tags alone chose — <see cref="HostsTheGroup(string,string)"/>.
    /// </param>
    /// <param name="canHostWithoutLosingWifi">
    ///   Whether this phone can become group owner without dropping the Wi-Fi it is on. False on
    ///   hardware that cannot run its station and a Wi-Fi Direct group at once while the station sits on
    ///   a channel no group owner may use (a DFS/radar channel) — there, hosting costs the phone its own
    ///   internet, so it is the wrong phone to host when a peer can do it without that cost.
    /// </param>
    /// <param name="radioRefused">
    ///   Whether this phone was already told to host and its radio would not, at any channel.
    /// </param>
    /// <param name="standInReady">
    ///   Whether this phone was told to <i>join</i> but the peer it was told to join has plainly not
    ///   turned up, so it takes the role over.
    /// </param>
    /// <remarks>
    /// Tag order only <i>proposes</i> a host. A proposed host that would lose its Wi-Fi to host, or whose
    /// radio has refused, yields — it joins and lets the peer stand in. Convergence still rests on the
    /// tags: the yielding phone never counts itself toward standing in, so exactly one phone ends up
    /// hosting the group both derived from the same key.
    /// </remarks>
    public static bool HostsTheGroup(
        bool proposedHost, bool canHostWithoutLosingWifi, bool radioRefused, bool standInReady)
        => (proposedHost && canHostWithoutLosingWifi && !radioRefused) || standInReady;
}
