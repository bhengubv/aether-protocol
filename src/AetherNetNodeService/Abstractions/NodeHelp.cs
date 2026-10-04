// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNetNodeService;

/// <summary>What a person's help session is doing.</summary>
public enum HelpKind
{
    /// <summary>They need help.</summary>
    Help = 0,

    /// <summary>They are sharing their way with their guardians; no alarm.</summary>
    Walk = 1,

    /// <summary>They said they are safe, or have arrived.</summary>
    Safe = 2,
}

/// <summary>How a guardian's phone tells its owner that someone needs help.</summary>
public enum HelpAlert
{
    /// <summary>Sound, vibrate and show it. The default: it is an emergency for the guardian.</summary>
    Loud = 0,

    /// <summary>Show it without a sound, for a guardian who asked for quiet.</summary>
    Quiet = 1,
}

/// <summary>Which Bluetooth container carries the help message.</summary>
public enum HelpAdvertForm
{
    /// <summary>A standard advert under a registered 16-bit service ID — every phone can send and hear it.</summary>
    Registered16 = 0,

    /// <summary>AetherNet's own 128-bit service ID, which needs Bluetooth 5 extended adverts.</summary>
    AetherNet128 = 1,

    /// <summary>
    /// AetherNet's own ID, sent in two halves — the advert and the scan response a listening phone asks for. No
    /// registered ID, no Bluetooth 5, and every phone can send it and hear it, so this is what a phone uses unless
    /// it has something better.
    /// </summary>
    AetherNet128Pair = 2,
}

/// <summary>
/// The containers in the order they are preferred, best reach first. A registered ID would be one advert every phone
/// can hear, so it leads; two halves need nothing registered and reach every phone as well, so they come next; the
/// single long advert is last, because only a Bluetooth 5 phone can hear it.
/// </summary>
public static class HelpAdvertForms
{
    /// <summary>Best first. A device sends the first of these its radio can manage.</summary>
    public static readonly HelpAdvertForm[] Preferred =
    [
        HelpAdvertForm.Registered16,
        HelpAdvertForm.AetherNet128Pair,
        HelpAdvertForm.AetherNet128,
    ];
}

/// <summary>Which of a person's own actions start Quiet help. Nothing the phone receives ever does.</summary>
[Flags]
public enum HelpTrigger
{
    None = 0,

    /// <summary>The button on the app's own screen.</summary>
    AppButton = 1,

    /// <summary>A held press on AetherNetService's notification.</summary>
    Notification = 2,

    /// <summary>The power button, pressed several times in a row.</summary>
    PowerButton = 4,

    /// <summary>Shaking the phone.</summary>
    Shake = 8,

    /// <summary>A second PIN that opens the app as usual and asks for help at the same time.</summary>
    DuressPin = 16,
}

/// <summary>How close the person's phone sounds to a guardian who is looking for them.</summary>
public enum HelpFindCue
{
    /// <summary>Screaming loud — in a pocket or the same bag, not metres.</summary>
    VeryClose = 0,
    Closer = 1,
    Further = 2,
    Same = 3,

    /// <summary>Not enough packets yet to compare.</summary>
    Waiting = 4,

    /// <summary>Nothing heard for a few seconds.</summary>
    Quiet = 5,

    /// <summary>Gone from the air.</summary>
    Gone = 6,
}

/// <summary>Someone the person chose to ask for help, and how that guardian's phone should tell them.</summary>
/// <param name="Tag">The guardian's AetherTag — where the help message is sent.</param>
/// <param name="Name">What the person calls them.</param>
/// <param name="Alert">Loud unless that guardian asked for quiet.</param>
public sealed record HelpGuardian(AetherNetTag Tag, string Name, HelpAlert Alert = HelpAlert.Loud);

/// <summary>Which triggers the person turned on, and how sensitive each is.</summary>
/// <param name="Enabled">The ones that are on.</param>
/// <param name="PowerPresses">How many power-button presses in a row.</param>
/// <param name="PowerWindowMs">How long they have to make them.</param>
/// <param name="ShakeThreshold">Total acceleration that counts as a shake, m/s² (gravity alone is about 9.8).</param>
/// <param name="ShakeCount">How many shakes.</param>
/// <param name="ShakeWindowMs">How long they have to make them.</param>
/// <param name="HoldSeconds">Seconds to call it off before it goes. Zero sends at once.</param>
public sealed record HelpTriggers(
    HelpTrigger Enabled = HelpTrigger.AppButton | HelpTrigger.Notification | HelpTrigger.PowerButton | HelpTrigger.Shake,
    int PowerPresses = 5,
    int PowerWindowMs = 3_000,
    double ShakeThreshold = 25.0,
    int ShakeCount = 3,
    int ShakeWindowMs = 1_500,
    int HoldSeconds = 0)
{
    public bool On(HelpTrigger trigger) => trigger != HelpTrigger.None && (Enabled & trigger) == trigger;
}

/// <summary>One way of putting the help message on the air, and whether this device can.</summary>
/// <param name="Form">The container.</param>
/// <param name="Available">Whether it can be used here and now.</param>
/// <param name="Chosen">Whether it is the one in use.</param>
/// <param name="Why">When it is not available, the plain reason — what the app shows.</param>
public sealed record HelpAdvertChoice(HelpAdvertForm Form, bool Available, bool Chosen, string? Why = null);

/// <summary>One point of a person's trail.</summary>
/// <param name="At">When.</param>
/// <param name="Lat">Where (WGS84).</param>
/// <param name="Lon">Where (WGS84).</param>
/// <param name="AccuracyM">How sure, in metres, when known.</param>
/// <param name="Reported">
/// True when the person's phone said where it was; false when it had no position and this is where the guardian's
/// phone was when it heard them.
/// </param>
/// <param name="Rssi">How loud it was, for a point this phone heard.</param>
public sealed record HelpPoint(DateTimeOffset At, double Lat, double Lon, int? AccuracyM, bool Reported, int? Rssi = null);

/// <summary>
/// Quiet help as it stands on this device: what this person is sending, if anything, and what they chose.
/// </summary>
public sealed record HelpState
{
    /// <summary>Whether a session is running now.</summary>
    public bool On { get; init; }

    /// <summary>What it says. <see cref="HelpKind.Safe"/> while the last "safe" message is still going out.</summary>
    public HelpKind Kind { get; init; } = HelpKind.Safe;

    /// <summary>When the person started it.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When they said they were safe.</summary>
    public DateTimeOffset? SafeAt { get; init; }

    /// <summary>The position going out, when the phone has one.</summary>
    public double? Lat { get; init; }

    /// <summary>The position going out, when the phone has one.</summary>
    public double? Lon { get; init; }

    /// <summary>How sure that position is, in metres.</summary>
    public int? AccuracyM { get; init; }

    /// <summary>When that position was taken.</summary>
    public DateTimeOffset? FixAt { get; init; }

    /// <summary>What the message says of the battery.</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>Whether it is going out over Bluetooth to phones close by.</summary>
    public bool Nearby { get; init; }

    /// <summary>How many guardians the mesh has taken it for.</summary>
    public int GuardiansReached { get; init; }

    /// <summary>The guardians the person chose.</summary>
    public IReadOnlyList<HelpGuardian> Guardians { get; init; } = [];

    /// <summary>The triggers they turned on.</summary>
    public HelpTriggers Triggers { get; init; } = new();

    /// <summary>Each Bluetooth container, whether it can be used here, and which is in use.</summary>
    public IReadOnlyList<HelpAdvertChoice> Adverts { get; init; } = [];

    /// <summary>Why it cannot be sent at all, when it cannot — no guardians chosen, no radio.</summary>
    public string? Why { get; init; }
}

/// <summary>Someone this device is a guardian for, and what it knows of their help or walk.</summary>
public sealed record HelpWatchCase
{
    /// <summary>Their AetherTag.</summary>
    public required AetherNetTag Person { get; init; }

    /// <summary>What this person calls them.</summary>
    public required string Name { get; init; }

    /// <summary>Help or Walk while it lasts; Safe once they say so.</summary>
    public required HelpKind Kind { get; init; }

    /// <summary>How this phone should tell its owner.</summary>
    public HelpAlert Alert { get; init; } = HelpAlert.Loud;

    /// <summary>When this phone first heard this session.</summary>
    public DateTimeOffset FirstHeardAt { get; init; }

    /// <summary>When it last heard anything of it.</summary>
    public DateTimeOffset LastHeardAt { get; init; }

    /// <summary>When it last heard them over Bluetooth, close by; null when only the mesh carried it.</summary>
    public DateTimeOffset? LastNearbyAt { get; init; }

    /// <summary>Where they last said they were.</summary>
    public double? Lat { get; init; }

    /// <summary>Where they last said they were.</summary>
    public double? Lon { get; init; }

    /// <summary>How sure that was, in metres.</summary>
    public int? AccuracyM { get; init; }

    /// <summary>When that position was taken.</summary>
    public DateTimeOffset? FixAt { get; init; }

    /// <summary>Their phone's battery, as last said.</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>When they said they were safe.</summary>
    public DateTimeOffset? SafeAt { get; init; }

    /// <summary>How close they sound, for a guardian walking towards them.</summary>
    public HelpFindCue Find { get; init; } = HelpFindCue.Waiting;

    /// <summary>How loud they were when last heard close by.</summary>
    public int? Rssi { get; init; }

    /// <summary>Their breadcrumbs, oldest first.</summary>
    public IReadOnlyList<HelpPoint> Trail { get; init; } = [];

    /// <summary>They have said they are safe.</summary>
    public bool IsSafe => Kind == HelpKind.Safe;
}

/// <summary>Everything an app needs to draw Quiet help: this person's own session, and the people they watch over.</summary>
public sealed record HelpReport
{
    public static readonly HelpReport None = new();

    /// <summary>What this device is sending, and what the person chose.</summary>
    public HelpState Mine { get; init; } = new();

    /// <summary>The people who chose this device's owner as a guardian and are asking for help now.</summary>
    public IReadOnlyList<HelpWatchCase> Watching { get; init; } = [];
}
