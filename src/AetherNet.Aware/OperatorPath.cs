// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/FieldwatchApp.kt (recordOperatorFix, acceptFix, the
// operator path) (github.com/offgridpete/fieldwatch, cf6562d). Reading the fix from the platform's location service
// belongs to the host.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>
/// This device's own walk: the recent position fixes that "moving with you" compares a radio's trail against.
/// </summary>
public sealed class OperatorPath
{
    /// <summary>A fix vaguer than this is not used.</summary>
    public const double MaxAccuracyM = 75.0;

    /// <summary>A fix older than this is not used.</summary>
    public const long MaxFixAgeMs = 30_000L;

    /// <summary>A fix closer than this to the last point replaces it instead of adding one.</summary>
    public const double SameSpotM = 15.0;

    public const int MaxPoints = 80;

    private readonly object _gate = new();
    private readonly List<GpsSample> _points = [];
    private readonly TimeProvider _time;
    private double _lengthM;

    public OperatorPath(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How far the kept points run, in metres.</summary>
    public double LengthM
    {
        get
        {
            lock (_gate)
            {
                return _lengthM;
            }
        }
    }

    /// <summary>
    /// A position fix from the platform. Used when it is accurate to <see cref="MaxAccuracyM"/> (or its accuracy is
    /// unknown) and no older than <see cref="MaxFixAgeMs"/>. True when it was added to the walk.
    /// </summary>
    public bool Accept(double lat, double lon, long fixAt, float? accuracyM)
    {
        if (accuracyM > MaxAccuracyM)
        {
            return false;
        }
        if (_time.GetUtcNow().ToUnixTimeMilliseconds() - fixAt > MaxFixAgeMs)
        {
            return false;
        }
        return Record(lat, lon, fixAt);
    }

    /// <summary>
    /// Adds a point. A hop no one could have made (see <see cref="Geo.HopPlausible"/>) is ignored; a point within
    /// <see cref="SameSpotM"/> of the last replaces it; the walk keeps its latest <see cref="MaxPoints"/> points.
    /// True when the walk changed.
    /// </summary>
    public bool Record(double lat, double lon, long at)
    {
        lock (_gate)
        {
            var last = _points.Count > 0 ? _points[^1] : null;
            if (last is not null && !Geo.HopPlausible(last, lat, lon, at))
            {
                return false;
            }
            if (last is not null && Geo.Meters(last.Lat, last.Lon, lat, lon) < SameSpotM)
            {
                _points[^1] = new GpsSample(at, lat, lon);
                return true;
            }
            if (last is not null)
            {
                _lengthM += Geo.Meters(last.Lat, last.Lon, lat, lon);
            }
            _points.Add(new GpsSample(at, lat, lon));
            if (_points.Count > MaxPoints)
            {
                _points.RemoveAt(0);
                _lengthM = Geo.PathLengthM(_points);
            }
            return true;
        }
    }

    /// <summary>The walk so far, oldest first.</summary>
    public IReadOnlyList<GpsSample> Copy()
    {
        lock (_gate)
        {
            return [.. _points];
        }
    }

    /// <summary>Forgets the walk (a new session).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _points.Clear();
            _lengthM = 0.0;
        }
    }
}
