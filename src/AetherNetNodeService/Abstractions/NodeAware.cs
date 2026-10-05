// SPDX-License-Identifier: MIT

namespace AetherNetNodeService;

/// <summary>Which radio a thing was heard over.</summary>
public enum AwareRadio
{
    /// <summary>Bluetooth — most things around a person, and everything that can move with them.</summary>
    Bluetooth = 0,

    /// <summary>Wi-Fi — the access points about the place, which stay where they are.</summary>
    WiFi = 1,
}

/// <summary>
/// How close a thing is, as a person would say it rather than in decibels. A radio's loudness is a poor ruler —
/// walls, pockets and bodies all move it — so it is given as three steps, which is about as much as it can honestly
/// carry.
/// </summary>
public enum AwareCloseness
{
    /// <summary>Loud. In this room, within a few paces.</summary>
    Here = 0,

    /// <summary>Middling. Nearby — the next room, across a shop.</summary>
    Near = 1,

    /// <summary>Faint. Somewhere about, at the edge of hearing.</summary>
    Far = 2,
}

/// <summary>
/// One thing Aether Aware can hear, as a person is shown it. Deliberately not the whole sighting: an address, a
/// loudness history and a vendor OUI are how it is worked out, not what anybody wants to read.
/// </summary>
public sealed record AwareThing
{
    /// <summary>This device's own handle for it, steady for as long as it is heard. Not for showing.</summary>
    public required string Id { get; init; }

    /// <summary>What to call it: what the signature pack named it, else what it calls itself, else its address.</summary>
    public required string Name { get; init; }

    /// <summary>What kind of thing it is in plain words — "Apple AirTags", "Microsoft Device" — when it is known.</summary>
    public string? What { get; init; }

    /// <summary>Which radio heard it.</summary>
    public AwareRadio Radio { get; init; }

    /// <summary>How close, in the three steps a person can act on.</summary>
    public AwareCloseness Closeness { get; init; }

    /// <summary>
    /// True when this has kept up with the person while they moved — the thing worth telling somebody, because a
    /// tracker in a bag follows and a fridge does not.
    /// </summary>
    public bool MovingWithYou { get; init; }

    /// <summary>Whether it is a finder tag by trade — an AirTag, a Tile — rather than merely something that followed.</summary>
    public bool FinderTag { get; init; }

    /// <summary>When it was first heard this session.</summary>
    public DateTimeOffset FirstHeard { get; init; }

    /// <summary>When it was last heard.</summary>
    public DateTimeOffset LastHeard { get; init; }

    /// <summary>True once it has not been heard for a while: still listed, so a person sees it leave.</summary>
    public bool Gone { get; init; }
}

/// <summary>
/// What Aether Aware has heard, as a screen shows it. The node does the hearing and the naming; an app renders this
/// and nothing else.
/// </summary>
public sealed record AwareReport
{
    /// <summary>Nothing heard, and nothing wrong — a device with no Aware at all.</summary>
    public static readonly AwareReport None = new();

    /// <summary>Whether Aware is listening right now.</summary>
    public bool On { get; init; }

    /// <summary>
    /// Why it is not listening, in words a person reads, when it is not. Null when it is. A permission not allowed,
    /// a radio switched off, a device with none.
    /// </summary>
    public string? Why { get; init; }

    /// <summary>
    /// True when <see cref="Why"/> is something the person can put right on this device's own pages, so an app can
    /// offer the way there. False when it is about the device itself and no tapping will change it.
    /// </summary>
    public bool Fixable { get; init; }

    /// <summary>Everything heard lately, loudest first, including what has just gone.</summary>
    public IReadOnlyList<AwareThing> Things { get; init; } = [];

    /// <summary>How many are being heard now.</summary>
    public int Heard { get; init; }

    /// <summary>How many of those the signature pack could name.</summary>
    public int Named { get; init; }

    /// <summary>How many have moved with this person.</summary>
    public int MovingWithYou { get; init; }

    /// <summary>How far this phone has walked while listening, in metres — what "moving with you" is measured against.</summary>
    public double WalkedM { get; init; }

    /// <summary>When this was last worked out.</summary>
    public DateTimeOffset? At { get; init; }
}
