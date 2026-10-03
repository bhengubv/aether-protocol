// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Geo.kt (Geo, CoTravel)
// (github.com/offgridpete/fieldwatch, cf6562d). Sit legs, map cells and on-screen masking are not ported.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Collections.Concurrent;

namespace AetherNet.Aware;

/// <summary>Distances and paths on the ground (WGS84).</summary>
public static class Geo
{
    /// <summary>About 150 km/h. Walking and city driving stay under this; a GPS glitch does not.</summary>
    public const double SpikeMaxSpeedMps = 42.0;

    public const double SpikeMinHopM = 40.0;

    public const long SpikeMinDtMs = 800L;

    private const double EarthRadiusM = 6_371_000.0;
    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>Metres between two points (haversine).</summary>
    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * DegreesToRadians;
        var p2 = lat2 * DegreesToRadians;
        var dp = (lat2 - lat1) * DegreesToRadians;
        var dl = (lon2 - lon1) * DegreesToRadians;
        var a = (Math.Sin(dp / 2) * Math.Sin(dp / 2)) + (Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2));
        return 2 * EarthRadiusM * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double PathLengthM(IReadOnlyList<GpsSample> samples)
    {
        var sum = 0.0;
        for (var i = 1; i < samples.Count; i++)
        {
            sum += Meters(samples[i - 1].Lat, samples[i - 1].Lon, samples[i].Lat, samples[i].Lon);
        }
        return sum;
    }

    /// <summary>The widest distance between any two samples.</summary>
    public static double SpanM(IReadOnlyList<GpsSample> samples)
    {
        var max = 0.0;
        for (var i = 0; i < samples.Count; i++)
        {
            for (var j = i + 1; j < samples.Count; j++)
            {
                max = Math.Max(max, Meters(samples[i].Lat, samples[i].Lon, samples[j].Lat, samples[j].Lon));
            }
        }
        return max;
    }

    /// <summary>Could the device really have moved from <paramref name="from"/> to here by <paramref name="at"/>?</summary>
    public static bool HopPlausible(GpsSample from, double lat, double lon, long at, double maxSpeedMps = SpikeMaxSpeedMps)
    {
        ArgumentNullException.ThrowIfNull(from);
        var d = Meters(from.Lat, from.Lon, lat, lon);
        var dt = at - from.At;
        if (d <= SpikeMinHopM || dt < SpikeMinDtMs)
        {
            return true;
        }
        var mps = d / Math.Max(dt / 1000.0, 0.001);
        return mps <= maxSpeedMps;
    }

    /// <summary>
    /// Drops GPS glitches: a point that shoots out and back, or a hop faster than <see cref="SpikeMaxSpeedMps"/>.
    /// </summary>
    public static IReadOnlyList<GpsSample> DespikePath(IReadOnlyList<GpsSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2)
        {
            return samples;
        }
        var current = samples;
        for (var round = 0; round < 4; round++)
        {
            var next = DespikeOnce(current);
            if (next.Count == current.Count)
            {
                return next;
            }
            current = next;
            if (current.Count < 2)
            {
                return current;
            }
        }
        return current;
    }

    private static IReadOnlyList<GpsSample> DespikeOnce(IReadOnlyList<GpsSample> samples)
    {
        if (samples.Count < 2)
        {
            return samples;
        }
        var output = new List<GpsSample>(samples.Count) { samples[0] };
        for (var i = 1; i < samples.Count; i++)
        {
            var prev = output[^1];
            var cur = samples[i];
            if (i + 1 < samples.Count)
            {
                var next = samples[i + 1];
                var dAb = Meters(prev.Lat, prev.Lon, cur.Lat, cur.Lon);
                var dBc = Meters(cur.Lat, cur.Lon, next.Lat, next.Lon);
                var dAc = Meters(prev.Lat, prev.Lon, next.Lat, next.Lon);
                var spike = dAb > SpikeMinHopM && dBc > SpikeMinHopM && dAc < dAb * 0.4 && dAc < dBc * 0.4;
                if (spike)
                {
                    continue;
                }
            }
            if (!HopPlausible(prev, cur.Lat, cur.Lon, cur.At))
            {
                continue;
            }
            output.Add(cur);
        }
        return output.Count >= 2 ? output : [samples[0], samples[^1]];
    }

    /// <summary>
    /// Adds a point to a radio's trail unless it is within 8 m of the last, or within 18 m and 30 s; then spreads the
    /// trail to at most <paramref name="cap"/> points. Returns the same list when nothing was added.
    /// </summary>
    public static IReadOnlyList<GpsSample> Append(IReadOnlyList<GpsSample> trail, long at, double lat, double lon, int rssi, int cap = 48)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (trail.Count > 0)
        {
            var last = trail[^1];
            var d = Meters(last.Lat, last.Lon, lat, lon);
            if (d < 8.0 || (d < 18.0 && at - last.At < 30_000L))
            {
                return trail;
            }
        }
        List<GpsSample> next = [.. trail, new GpsSample(at, lat, lon, rssi)];
        return CapSpread(next, cap);
    }

    /// <summary>
    /// Keeps <paramref name="cap"/> samples spread across the whole trail (first, last, even steps between), so a
    /// long sit does not push the start out.
    /// </summary>
    public static IReadOnlyList<GpsSample> CapSpread(IReadOnlyList<GpsSample> samples, int cap)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count <= cap)
        {
            return samples;
        }
        if (cap <= 1)
        {
            return [samples[^1]];
        }
        if (cap == 2)
        {
            return [samples[0], samples[^1]];
        }
        var lastIdx = samples.Count - 1;
        var output = new List<GpsSample>(cap);
        for (var i = 0; i < cap; i++)
        {
            var s = samples[i * lastIdx / (cap - 1)];
            if (output.Count == 0 || output[^1].At != s.At)
            {
                output.Add(s);
            }
        }
        if (output[^1].At != samples[^1].At)
        {
            output.Add(samples[^1]);
        }
        return output;
    }
}

/// <summary>
/// "Moving with you": a Bluetooth radio that has stayed loud along this device's path. Wi-Fi access points are left
/// out — a loud one driven past paints hundreds of metres of the path and would look as if it came along.
/// </summary>
public static class CoTravel
{
    /// <summary>How far this device must have moved before anything can be said to move with it.</summary>
    public const double MoveM = 45.0;

    public const double NearM = 50.0;

    /// <summary>Not heard for longer than this, it is not with you.</summary>
    public const long HeardMs = 90_000L;

    public const int LoudDbm = -70;

    /// <summary>The trail's loudness floor: two thirds of its points must be at least this loud.</summary>
    public const int TrailLoudDbm = -75;

    private static readonly ConcurrentDictionary<string, TrailGeom> TrailGeometry = new(StringComparer.Ordinal);

    /// <summary>This device's walk: how far, over how long, and where it is now.</summary>
    public sealed record Ctx(double PathLengthM, long DurationMs, GpsSample? Here, bool Ready)
    {
        public static readonly Ctx None = new(0.0, 0L, null, false);

        /// <summary>The context for a path (oldest first). Ready once the path is at least <see cref="MoveM"/> long.</summary>
        public static Ctx Of(IReadOnlyList<GpsSample> path)
        {
            ArgumentNullException.ThrowIfNull(path);
            if (path.Count < 2)
            {
                return None;
            }
            var length = Geo.PathLengthM(path);
            var duration = Math.Max(path[^1].At - path[0].At, 0L);
            return new Ctx(length, duration, path[^1], length >= MoveM);
        }
    }

    /// <summary>
    /// Whether the radio has moved with this device: Bluetooth only; this device moved at least
    /// <see cref="MoveM"/>; the radio heard within <see cref="HeardMs"/>, at least <see cref="TrailLoudDbm"/> now and
    /// for two thirds of its trail; its trail itself spans and runs at least 27 m; and its last point is within
    /// max(50 m, 15 s at the current speed) + 25 m of where this device is.
    /// </summary>
    public static bool WithYou(Sighting device, Ctx ctx, long now)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(ctx);
        if (device.Kind == RadioKind.Wifi)
        {
            return false;
        }
        if (!ctx.Ready || ctx.Here is not { } here)
        {
            return false;
        }
        if (now - device.LastSeen > HeardMs)
        {
            return false;
        }
        // The last packet can dip on a highway; the cheap reject is the trail floor, not the -70 instant.
        if (device.Rssi < TrailLoudDbm)
        {
            return false;
        }
        var trail = device.GpsTrail;
        if (trail.Count < 2)
        {
            return false;
        }
        var geom = GeometryFor(device.Key, trail);
        if (geom.Bbox < MoveM * 0.6)
        {
            return false;
        }
        var loud = trail.Count(s => s.Rssi >= TrailLoudDbm);
        if (loud < ((trail.Count * 2) + 2) / 3)
        {
            return false;
        }
        if (geom.Len < MoveM * 0.6)
        {
            return false;
        }
        var last = trail[^1];
        var moved = Geo.Meters(here.Lat, here.Lon, last.Lat, last.Lon);
        // The last stamp is this device at hear-time. 50 m of driving is ~2 s on a highway, so a bag tag that
        // advertises every few seconds needs slack that grows with speed.
        var speed = Math.Clamp(ctx.PathLengthM / Math.Max(ctx.DurationMs / 1000.0, 1.0), 0.0, 40.0);
        var allowM = Math.Max(NearM, speed * 15.0) + 25.0;
        return moved <= allowM;
    }

    private static TrailGeom GeometryFor(string key, IReadOnlyList<GpsSample> trail)
    {
        var last = trail[^1];
        if (TrailGeometry.TryGetValue(key, out var hit) &&
            hit.N == trail.Count && hit.At == last.At && hit.Lat == last.Lat && hit.Lon == last.Lon)
        {
            return hit;
        }
        var next = new TrailGeom(trail.Count, last.At, last.Lat, last.Lon, Geo.PathLengthM(trail), BboxSpanM(trail));
        TrailGeometry[key] = next;
        if (TrailGeometry.Count > 1024)
        {
            TrailGeometry.Clear();
        }
        return next;
    }

    private static double BboxSpanM(IReadOnlyList<GpsSample> samples)
    {
        double minLat = samples[0].Lat, maxLat = minLat, minLon = samples[0].Lon, maxLon = minLon;
        for (var i = 1; i < samples.Count; i++)
        {
            minLat = Math.Min(minLat, samples[i].Lat);
            maxLat = Math.Max(maxLat, samples[i].Lat);
            minLon = Math.Min(minLon, samples[i].Lon);
            maxLon = Math.Max(maxLon, samples[i].Lon);
        }
        return Geo.Meters(minLat, minLon, maxLat, maxLon);
    }

    private sealed record TrailGeom(int N, long At, double Lat, double Lon, double Len, double Bbox);
}
