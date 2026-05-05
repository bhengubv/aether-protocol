// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Aether.Diagnostics;

/// <summary>
/// Allocation-free stopwatch built on <see cref="Stopwatch.GetTimestamp"/>.
/// Identical in spirit to the well-known internal <c>ValueStopwatch</c>
/// pattern from ASP.NET Core / Microsoft.Extensions.* (and recently
/// promoted as <c>Stopwatch.GetElapsedTime</c> in .NET 7+). Used here so
/// the per-message hot paths add zero managed allocations even when
/// telemetry is being recorded.
///
/// <para>
/// Usage: <c>var sw = ValueStopwatch.StartNew(); ... ; var ms = sw.GetElapsedMilliseconds();</c>
/// </para>
/// </summary>
public readonly struct ValueStopwatch
{
    private static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

    private readonly long _startTimestamp;

    private ValueStopwatch(long startTimestamp) => _startTimestamp = startTimestamp;

    /// <summary>True if this struct was constructed via <see cref="StartNew"/> rather than the default constructor.</summary>
    public bool IsActive => _startTimestamp != 0;

    /// <summary>Captures the current high-resolution timestamp.</summary>
    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    /// <summary>
    /// Returns elapsed milliseconds as a <see cref="double"/> (sub-ms precision
    /// preserved for histogram fidelity). Zero if the struct was never started.
    /// </summary>
    public double GetElapsedMilliseconds()
    {
        if (_startTimestamp == 0)
            return 0d;
        var delta = Stopwatch.GetTimestamp() - _startTimestamp;
        return delta * TimestampToMs;
    }
}
