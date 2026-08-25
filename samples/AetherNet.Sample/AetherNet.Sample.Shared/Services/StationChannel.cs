// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The channel this phone's own Wi-Fi is on — remembered, because the moment we need it is the moment
/// we cannot read it.
///
/// <para>
/// <b>Why this exists.</b> A phone has one radio. Hosting a group on a different channel from the
/// access point it is associated to asks that radio to be in two places at once, and the chip answers
/// by time-slicing — which, on the phone <i>joining</i>, means abandoning the network it was on. That
/// is not a debugging inconvenience: a stranger who loses their internet the instant they accept help
/// from a friend has been given a reason to back out, and they will take it.
/// </para>
///
/// <para>
/// <b>Why remembering is the whole trick.</b> Hosting takes the station down on some handsets — a P30
/// reports <c>Frequency: -1MHz</c> for as long as it is group owner. So asking "what channel are we
/// on" at the moment of hosting asks a phone that has already been disconnected by the previous
/// answer. Zero then means "no channel to match", the group lands on a default, the station never
/// recovers, and the next attempt asks the same disconnected phone and gets the same zero. The loop
/// closes on itself. A value banked while the answer was still true breaks it.
/// </para>
/// </summary>
public sealed class StationChannel
{
    /// <summary>Nothing below this is a Wi-Fi channel; it is an error code or a lie.</summary>
    /// <remarks>2.4GHz channel 1 sits at 2412MHz, so anything under 2400 is not a frequency.</remarks>
    public const int LowestReal = 2400;

    /// <summary>Nor is anything above this. 6GHz tops out well below it.</summary>
    public const int HighestReal = 7200;

    /// <summary>The last channel this phone's Wi-Fi was genuinely on, or zero if never seen.</summary>
    public int Known { get; private set; }

    /// <summary>
    /// Take a live reading and give back the best channel available.
    /// </summary>
    /// <param name="live">
    ///   What the radio says right now. Zero or nonsense when the station is down — which includes
    ///   every moment this phone is already hosting.
    /// </param>
    /// <returns>The live reading when it is real, otherwise the last real one, otherwise zero.</returns>
    /// <remarks>
    /// Deliberately not "remember only once". A phone moves between networks and bands all day, and a
    /// channel from this morning's café is worse than useless — it would put the group on a channel
    /// nothing here is using. The newest real answer always wins.
    /// </remarks>
    public int Best(int live)
    {
        if (IsReal(live)) Known = live;
        return Known;
    }

    /// <summary>Whether a reading is a channel at all.</summary>
    /// <remarks>
    /// A disconnected Android reports <c>-1</c>, and a clamp to zero elsewhere turns that into a
    /// number that looks like an answer. Both are rejected here rather than banked and then used to
    /// aim a radio.
    /// </remarks>
    public static bool IsReal(int frequency) => frequency is >= LowestReal and <= HighestReal;
}
