// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Hunt.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>How the hunted radio sounds now compared with a few seconds ago.</summary>
public enum HuntCue
{
    VeryClose,
    Closer,
    Further,
    Same,
    Waiting,
    Quiet,
    Gone,
}

/// <summary>
/// "Find it": walk towards one Bluetooth radio by how loud it is. Loudness is not distance and not direction; going
/// quiet or gone matters as much as getting louder.
/// </summary>
public static class Hunt
{
    public const long RecentMs = 2_000L;

    public const long EarlierFromMs = 8_000L;

    public const long EarlierToMs = 3_500L;

    public const double StepDb = 3.0;

    public const long QuietMs = 8_000L;

    /// <summary>In the hand, a pocket or the same bag — not a distance.</summary>
    public const double VeryCloseDbm = -45.0;

    public const int TickLoudDbm = -40;

    public const int TickQuietDbm = -90;

    public const long TickFastMs = 90L;

    public const long TickSlowMs = 1_400L;

    /// <summary>
    /// The cue from the radio's recent readings: the last 2 s against 3.5–8 s ago, a 3 dB step either way.
    /// </summary>
    public static HuntCue Cue(IReadOnlyList<RssiSample> samples, long now, long? lastSeen, bool missing)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (missing)
        {
            return HuntCue.Gone;
        }
        if (lastSeen is not { } seen)
        {
            return HuntCue.Waiting;
        }
        if (now - seen > QuietMs)
        {
            return HuntCue.Quiet;
        }
        var usable = samples.Where(s => Rssi.Measured(s.Rssi)).ToList();
        var recent = usable.Where(s => s.At >= now - RecentMs).ToList();
        double? loud = recent.Count > 0
            ? recent.Average(s => s.Rssi)
            : usable.LastOrDefault(s => now - s.At <= QuietMs)?.Rssi;
        if (loud >= VeryCloseDbm)
        {
            return HuntCue.VeryClose;
        }
        var earlier = usable.Where(s => s.At >= now - EarlierFromMs && s.At <= now - EarlierToMs).ToList();
        if (recent.Count < 2 || earlier.Count < 2)
        {
            return HuntCue.Waiting;
        }
        var delta = recent.Average(s => s.Rssi) - earlier.Average(s => s.Rssi);
        return delta >= StepDb ? HuntCue.Closer : delta <= -StepDb ? HuntCue.Further : HuntCue.Same;
    }

    /// <summary>Fieldwatch's words for the cue.</summary>
    public static string Label(HuntCue cue) => cue switch
    {
        HuntCue.VeryClose => "Very Close",
        HuntCue.Closer => "Closer",
        HuntCue.Further => "Further",
        HuntCue.Same => "About the same",
        HuntCue.Waiting => "Listening…",
        HuntCue.Quiet => "Quiet",
        _ => "Gone",
    };

    public static string Hint(HuntCue cue) => cue switch
    {
        HuntCue.VeryClose => "Screaming loud here. Look around — usually in-hand, pocket, or the same bag. Not meters.",
        HuntCue.Closer => "Louder than a few seconds ago. Keep walking that way.",
        HuntCue.Further => "Quieter than a few seconds ago. Turn or back up.",
        HuntCue.Same => "No clear change yet. Slow down; hold the phone still.",
        HuntCue.Waiting => "Need a few seconds of packets to compare.",
        HuntCue.Quiet => "No packet for a few seconds. Silent, or behind a wall.",
        _ => "Left the live set. Randomized BLE often vanishes mid-hunt.",
    };

    /// <summary>
    /// Milliseconds between ticks, from 1,400 at -90 dBm to 90 at -40 dBm; null to stay silent (quiet, gone, or no
    /// reading).
    /// </summary>
    public static long? TickIntervalMs(int? rssi, HuntCue cue)
    {
        if (cue is HuntCue.Quiet or HuntCue.Gone || rssi is not { } r)
        {
            return null;
        }
        var span = (double)(TickLoudDbm - TickQuietDbm);
        var t = Math.Clamp((r - TickQuietDbm) / span, 0.0, 1.0);
        return (long)(TickSlowMs + ((TickFastMs - TickSlowMs) * t));
    }
}
