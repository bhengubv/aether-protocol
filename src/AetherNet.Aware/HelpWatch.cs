// SPDX-License-Identifier: MIT

namespace AetherNet.Aware;

/// <summary>Where a point of a trail came from.</summary>
public enum CrumbSource
{
    /// <summary>The person's phone said where it was.</summary>
    Reported,

    /// <summary>The person's phone had no position; this is where the guardian's phone was when it heard it.</summary>
    HeardNear,
}

/// <summary>One point of a person's trail.</summary>
public sealed record HelpCrumb(long At, double Lat, double Lon, int? AccuracyM, CrumbSource Source, int? Rssi = null);

/// <summary>One person's help or walk, as a guardian's phone knows it.</summary>
public sealed record HelpCase
{
    public required string PersonId { get; init; }

    public required string Name { get; init; }

    /// <summary>Help or Walk while it lasts; Safe once the person says so.</summary>
    public required HelpKind Kind { get; init; }

    public long FirstHeardAt { get; init; }

    public long LastHeardAt { get; init; }

    /// <summary>When the person's phone made the newest message heard (Unix ms).</summary>
    public long LastMessageAt { get; init; }

    /// <summary>When it was last heard over Bluetooth, close by; null when only the mesh carried it.</summary>
    public long? LastNearbyAt { get; init; }

    public double? Lat { get; init; }

    public double? Lon { get; init; }

    public int? AccuracyM { get; init; }

    /// <summary>When the newest position was taken (Unix ms).</summary>
    public long? FixAt { get; init; }

    public int? BatteryPercent { get; init; }

    public long? SafeAt { get; init; }

    /// <summary>The breadcrumbs, oldest first.</summary>
    public IReadOnlyList<HelpCrumb> Trail { get; init; } = [];

    /// <summary>How loud the person's phone was each time it was heard close by — what Find it walks by.</summary>
    public IReadOnlyList<RssiSample> Loudness { get; init; } = [];

    public bool IsSafe => Kind == HelpKind.Safe;

    /// <summary>Find it: closer, further, or quiet, from the recent loudness.</summary>
    public HuntCue FindIt(long now) => Hunt.Cue(Loudness, now, LastNearbyAt, missing: IsSafe);
}

/// <summary>
/// The guardians' side of Quiet help: knows the people who chose this phone's owner as a guardian, reads their help
/// messages (from Bluetooth adverts or the mesh), and keeps each one's trail until a day after it ends. A message from
/// anyone else is unreadable here and nothing of it is kept.
/// </summary>
public sealed class HelpWatch
{
    /// <summary>How long a trail is kept after it ends (safe) or after the person was last heard.</summary>
    public const long KeepAfterEndMs = 24 * 60 * 60 * 1000L;

    public const int TrailCap = 200;

    private const int LoudnessCap = 60;
    private const double SameSpotM = 5.0;
    private const long SameSpotMs = 30_000L;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Name, HelpKey Key)> _people = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HelpCase> _cases = new(StringComparer.Ordinal);

    public HelpWatch(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Someone chose this phone's owner as a guardian and shared their help key.</summary>
    public void Watch(string personId, string name, HelpKey key)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            _people[personId] = (name, key);
        }
    }

    /// <summary>No longer a guardian for this person: forget them and their trail.</summary>
    public void Unwatch(string personId)
    {
        lock (_gate)
        {
            _people.Remove(personId);
            _cases.Remove(personId);
        }
    }

    public IReadOnlyList<HelpCase> Cases
    {
        get
        {
            lock (_gate)
            {
                return [.. _cases.Values];
            }
        }
    }

    public HelpCase? Find(string personId)
    {
        lock (_gate)
        {
            return _cases.GetValueOrDefault(personId);
        }
    }

    /// <summary>
    /// A help message, heard over Bluetooth (give its loudness, and where this phone is) or delivered over the mesh.
    /// Returns the person's case when it is from someone this phone watches over; otherwise null, and nothing is kept.
    /// </summary>
    public HelpCase? Hear(ReadOnlySpan<byte> payload, int? rssi = null, GpsSample? whereIAm = null)
    {
        lock (_gate)
        {
            var now = Now();
            foreach (var (personId, (name, key)) in _people)
            {
                if (HelpCodec.TryRead(key, payload, now) is not { } reading)
                {
                    continue;
                }
                var level = rssi is { } r && Rssi.Measured(r) ? r : (int?)null;
                var updated = Merge(_cases.GetValueOrDefault(personId), personId, name, reading, now, level, whereIAm);
                _cases[personId] = updated;
                return updated;
            }
            return null;
        }
    }

    /// <summary>Forgets trails that ended, or went unheard, more than <see cref="KeepAfterEndMs"/> ago.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            var now = Now();
            foreach (var (personId, c) in _cases.ToList())
            {
                if (now - (c.SafeAt ?? c.LastHeardAt) > KeepAfterEndMs)
                {
                    _cases.Remove(personId);
                }
            }
        }
    }

    private static HelpCase Merge(
        HelpCase? existing,
        string personId,
        string name,
        HelpReading reading,
        long now,
        int? rssi,
        GpsSample? whereIAm)
    {
        var message = reading.Message;
        // A new help or walk after the last one ended starts a fresh case.
        if (existing is { IsSafe: true } && message.Kind != HelpKind.Safe && reading.MadeAt > existing.LastMessageAt)
        {
            existing = null;
        }
        var current = existing ?? new HelpCase
        {
            PersonId = personId,
            Name = name,
            Kind = message.Kind,
            FirstHeardAt = now,
            LastHeardAt = now,
            LastMessageAt = reading.MadeAt,
        };

        // Heard close by: the loudness counts even when the advert repeats a message already read.
        current = current with
        {
            Name = name,
            LastHeardAt = now,
            Loudness = rssi is { } level ? Capped([.. current.Loudness, new RssiSample(now, level)], LoudnessCap) : current.Loudness,
            LastNearbyAt = rssi is null ? current.LastNearbyAt : now,
        };
        if (existing is not null && reading.MadeAt <= existing.LastMessageAt)
        {
            // The same message again, or an older one heard late: nothing more to learn from it.
            return current;
        }

        var trail = current.Trail;
        var lat = current.Lat;
        var lon = current.Lon;
        var accuracy = current.AccuracyM;
        var fixAt = current.FixAt;
        if (message.HasPosition)
        {
            lat = message.Lat;
            lon = message.Lon;
            accuracy = message.AccuracyM;
            fixAt = reading.MadeAt - ((message.FixAgeSeconds ?? 0) * 1000L);
            trail = AddCrumb(trail, new HelpCrumb(fixAt.Value, lat!.Value, lon!.Value, accuracy, CrumbSource.Reported));
        }
        else if (whereIAm is not null && PayloadLocation.ValidCoord(whereIAm.Lat, whereIAm.Lon))
        {
            trail = AddCrumb(trail, new HelpCrumb(now, whereIAm.Lat, whereIAm.Lon, null, CrumbSource.HeardNear, rssi));
        }
        return current with
        {
            Kind = message.Kind,
            LastMessageAt = reading.MadeAt,
            Lat = lat,
            Lon = lon,
            AccuracyM = accuracy,
            FixAt = fixAt,
            BatteryPercent = message.BatteryPercent ?? current.BatteryPercent,
            SafeAt = message.Kind == HelpKind.Safe ? current.SafeAt ?? now : null,
            Trail = trail,
        };
    }

    /// <summary>Adds a point unless it is the same spot as the last one from the same source, moments ago.</summary>
    private static IReadOnlyList<HelpCrumb> AddCrumb(IReadOnlyList<HelpCrumb> trail, HelpCrumb crumb)
    {
        var last = trail.LastOrDefault(c => c.Source == crumb.Source);
        if (last is not null &&
            Math.Abs(crumb.At - last.At) < SameSpotMs &&
            Geo.Meters(last.Lat, last.Lon, crumb.Lat, crumb.Lon) < SameSpotM)
        {
            return trail;
        }
        return Spread([.. trail.Append(crumb).OrderBy(c => c.At)], TrailCap);
    }

    /// <summary>Keeps <paramref name="cap"/> points spread over the whole trail: the first, the last, and even steps between.</summary>
    private static IReadOnlyList<HelpCrumb> Spread(List<HelpCrumb> crumbs, int cap)
    {
        if (crumbs.Count <= cap)
        {
            return crumbs;
        }
        var lastIdx = crumbs.Count - 1;
        var output = new List<HelpCrumb>(cap);
        for (var i = 0; i < cap; i++)
        {
            output.Add(crumbs[(int)((long)i * lastIdx / (cap - 1))]);
        }
        return output;
    }

    private static IReadOnlyList<RssiSample> Capped(List<RssiSample> samples, int cap) =>
        samples.Count <= cap ? samples : samples.GetRange(samples.Count - cap, cap);

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
}
