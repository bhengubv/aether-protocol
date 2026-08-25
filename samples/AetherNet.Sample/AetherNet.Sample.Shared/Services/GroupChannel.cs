// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Which channel to put the group on, and what to try next when the radio says no.
///
/// <para>
/// <b>The bug this exists for, measured on a P30.</b> The phone's own Wi-Fi was on 5500 MHz. That is a
/// radar channel and Wi-Fi Direct is barred from it, so we asked for the 5 GHz band instead; the radio
/// refused that too, and the retry then dropped <i>every</i> preference. The group landed on 2.4 GHz
/// while the house network was on 5 GHz — and a phone has one radio, so the friend joining had to
/// abandon their Wi-Fi to follow us. Their internet went, and losing your internet the instant you
/// accept help from somebody is a reason to back out.
/// </para>
///
/// <para>
/// The fault was the shape of the retry: one preference, then none. A ladder keeps as much of the
/// preference as the radio will accept — the exact channel, then a legal channel in the same band,
/// then a band, then whatever it likes. Every rung is better for the person joining than the rung
/// below it.
/// </para>
/// </summary>
public static class GroupChannel
{
    /// <summary>2.4 GHz runs from channel 1 to 14.</summary>
    public const int LowBandFrom = 2400, LowBandTo = 2500;

    /// <summary>The radar-shared stretch of 5 GHz — channels 52 to 144. Barred to a group owner.</summary>
    /// <remarks>
    /// Regulators require a device here to listen for radar before transmitting and to vacate if it
    /// hears any. A Wi-Fi Direct group owner cannot do that, so it is simply not allowed — and the
    /// framework does not refuse the request, it ignores it and puts the group wherever it likes.
    /// </remarks>
    public const int RadarFrom = 5260, RadarTo = 5720;

    /// <summary>What we ask for in 5 GHz when the phone's own channel is off limits.</summary>
    /// <remarks>
    /// Channel 149. Above the radar range, allowed to a group owner nearly everywhere, and far enough
    /// from the low channels that a phone associated at 5500 does not have to change band to reach it.
    /// </remarks>
    public const int HighFallback = 5745;

    /// <summary>And in 2.4 GHz. Channel 6, the middle one, universally legal.</summary>
    public const int LowFallback = 2437;

    /// <summary>Ask the framework for nothing and take what it gives.</summary>
    public const int Anything = 0;

    /// <summary>Whether a group owner is allowed to sit on this channel.</summary>
    public static bool Allowed(int mhz) =>
        mhz is >= LowBandFrom and <= LowBandTo ||
        (mhz > LowBandTo && mhz is < RadarFrom or > RadarTo);

    /// <summary>Whether this channel is in the radar-shared stretch.</summary>
    public static bool IsRadar(int mhz) => mhz is >= RadarFrom and <= RadarTo;

    /// <summary>
    /// The channels to try, best first, for a phone currently associated on <paramref name="station"/>.
    /// </summary>
    /// <param name="station">
    ///   The channel this phone's own Wi-Fi is on, or zero when it has none.
    /// </param>
    /// <remarks>
    /// <para>
    /// Ordered by what it costs the person joining. Sharing the exact channel costs them nothing —
    /// their phone stays where it is. Staying in the same band costs them a channel change. Changing
    /// band costs them the network, which is the outcome that made this file necessary.
    /// </para>
    /// <para>
    /// Ends with <see cref="Anything"/> deliberately: a group on a channel we did not choose is still
    /// better than no group, and a person who has just pressed a button deserves something to happen.
    /// </para>
    /// </remarks>
    public static int[] Ladder(int station)
    {
        if (!StationChannel.IsReal(station)) return [LowFallback, Anything];

        var rungs = new List<int>(4);

        // Their phone does not move at all.
        if (Allowed(station)) rungs.Add(station);

        // Same band, legal channel — a channel change rather than a band change.
        if (station > LowBandTo)
        {
            if (HighFallback != station) rungs.Add(HighFallback);
        }
        else if (LowFallback != station)
        {
            rungs.Add(LowFallback);
        }

        // The other band. Costs them the network, but a phone with dual-band concurrency may keep both.
        rungs.Add(station > LowBandTo ? LowFallback : HighFallback);

        rungs.Add(Anything);
        return [.. rungs];
    }

    /// <summary>What to say about a rung, so a log reads as a decision rather than a retry.</summary>
    public static string Describe(int mhz, int station) =>
        mhz == Anything ? "anywhere the radio likes — their phone will have to leave your Wi-Fi"
        : mhz == station ? $"{mhz}MHz — the channel this phone is already on, so nothing has to move"
        : (mhz > LowBandTo) == (station > LowBandTo)
            ? $"{mhz}MHz — same band, so their phone changes channel but keeps the band"
            : $"{mhz}MHz — the other band, which will cost them their Wi-Fi";
}
