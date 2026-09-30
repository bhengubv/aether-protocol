// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Host;

/// <summary>
/// Which contact a phone points its pair-by-pair radios at — Bluetooth and Wi-Fi Direct, which meet one person at
/// a time — as opposed to the shared Wi-Fi, where every contact is met at once.
/// </summary>
public static class MeetingHost
{
    /// <summary>
    /// The contact to point the radios at: the lowest-sorting one who is actually here; with nobody here, the one
    /// they already point at, while that is still a contact; otherwise the lowest-sorting of all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lowest-sorting is a rule both phones apply without a word passing between them, so they agree on whom to
    /// meet. It used to be applied to every contact, there or not — so one absent contact who happened to sort
    /// lowest had a phone's radios pointed at nobody, again every half-minute, while the person in the room was
    /// left to the Wi-Fi alone (P30 and Pixel, 2026-09-30). Among the people actually here the rule still agrees
    /// on both sides, because each phone sees the other.
    /// </para>
    /// <para>
    /// With nobody here, the radios stay on whoever they were already on — the likeliest person to come back —
    /// rather than swinging to somebody absent every time a link drops.
    /// </para>
    /// </remarks>
    /// <param name="contacts">The contacts' tags.</param>
    /// <param name="isHere">Whether a contact is linked right now, on any radio.</param>
    /// <param name="current">Whom the radios point at now, or null.</param>
    /// <returns>The contact to meet, or null when there are no contacts.</returns>
    public static string? Choose(IEnumerable<string> contacts, Func<string, bool> isHere, string? current)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(isHere);

        string? lowest = null;
        string? lowestHere = null;
        var currentIsAContact = false;

        foreach (var tag in contacts)
        {
            if (string.IsNullOrEmpty(tag)) continue;

            if (lowest is null || string.CompareOrdinal(tag, lowest) < 0) lowest = tag;
            if (string.Equals(tag, current, StringComparison.Ordinal)) currentIsAContact = true;

            if ((lowestHere is null || string.CompareOrdinal(tag, lowestHere) < 0) && isHere(tag)) lowestHere = tag;
        }

        return lowestHere ?? (currentIsAContact ? current : lowest);
    }
}
