// SPDX-License-Identifier: MIT

namespace AetherNet.Aware;

/// <summary>What a help message says the person is doing.</summary>
public enum HelpKind : byte
{
    /// <summary>The person needs help.</summary>
    Help = 0,

    /// <summary>The person is sharing their way with their guardians; no alarm.</summary>
    Walk = 1,

    /// <summary>The person said they are safe, or have arrived: the help or the walk is over.</summary>
    Safe = 2,
}

/// <summary>What a help message carries.</summary>
public sealed record HelpMessage
{
    public required HelpKind Kind { get; init; }

    /// <summary>Where the person's phone is (WGS84), when it knows.</summary>
    public double? Lat { get; init; }

    public double? Lon { get; init; }

    /// <summary>How sure the position is, in metres: kept to the nearest 2 m, up to 508.</summary>
    public int? AccuracyM { get; init; }

    public int? BatteryPercent { get; init; }

    /// <summary>How old the position was when the message was made, in seconds, up to 254.</summary>
    public int? FixAgeSeconds { get; init; }

    /// <summary>Whether the message has a usable position.</summary>
    public bool HasPosition => PayloadLocation.ValidCoord(Lat, Lon);
}

/// <summary>A help message a guardian's phone read, and when the sending phone made it (Unix ms).</summary>
public sealed record HelpReading(HelpMessage Message, long MadeAt);
