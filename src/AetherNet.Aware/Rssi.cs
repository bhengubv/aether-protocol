// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Rssi.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>
/// Loudness as this device received it (dBm). Bluetooth uses 127 for "not available"; that is not a real reading.
/// </summary>
public static class Rssi
{
    public static bool Measured(int rssi) => rssi is >= -127 and <= 126;

    /// <summary>"-103 to -86 dBm", "-90 dBm", or "Not available".</summary>
    public static string SessionRange(int min, int max, IReadOnlyList<RssiSample>? history = null)
    {
        var values = new List<int>((history?.Count ?? 0) + 2);
        if (Measured(min))
        {
            values.Add(min);
        }
        if (Measured(max))
        {
            values.Add(max);
        }
        foreach (var s in history ?? [])
        {
            if (Measured(s.Rssi))
            {
                values.Add(s.Rssi);
            }
        }
        if (values.Count == 0)
        {
            return "Not available";
        }
        var lo = values.Min();
        var hi = values.Max();
        return lo == hi
            ? FormattableString.Invariant($"{lo} dBm")
            : FormattableString.Invariant($"{lo} to {hi} dBm");
    }

    /// <summary>This reading if it is real, else the latest real one in the history.</summary>
    public static int? LastMeasured(int rssi, IReadOnlyList<RssiSample> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (Measured(rssi))
        {
            return rssi;
        }
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (Measured(history[i].Rssi))
            {
                return history[i].Rssi;
            }
        }
        return null;
    }
}
