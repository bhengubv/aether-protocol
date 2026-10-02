// SPDX-License-Identifier: MIT

namespace AetherNetNodeService;

/// <summary>
/// A read-only snapshot of whether the node is reaching anyone right now and over which radios.
///
/// <para>
/// Synthesized by the host from the SDK's single-link and radio-choice types (<c>IMeshLink</c>,
/// <c>MeshWebService</c>, <c>RadioChoice</c>) — there is no single <c>IRadioMesh</c> in the SDK. It is a
/// report a bound app renders, never a control: nobody chooses a transport, the widest linked radio
/// carries.
/// </para>
/// </summary>
/// <param name="Linked">True when at least one radio has a peer on the other end right now.</param>
/// <param name="Radio">The human label of the carrying radio (e.g. "Wi-Fi Direct"), or null when not linked.</param>
/// <param name="Radios">Every radio the device has, with its current availability and link state.</param>
public sealed record NodeLinkStatus(bool Linked, string? Radio, IReadOnlyList<RadioStatus> Radios)
{
    /// <summary>The "not reaching anyone, no radios" snapshot — e.g. a host with no radio at all.</summary>
    public static NodeLinkStatus Offline { get; } = new(false, null, System.Array.Empty<RadioStatus>());

    /// <summary>How many of this device's radios are usable right now.</summary>
    public int Available
    {
        get
        {
            var n = 0;
            foreach (var r in Radios)
            {
                if (r.Available) n++;
            }
            return n;
        }
    }

    /// <summary>How many radios this device has in total (available or not).</summary>
    public int Total => Radios.Count;

    /// <summary>
    /// The permissions the phone keeps for AetherNetService, and whether the person has allowed each — the same list
    /// as AetherNetService's own page in the phone's settings. AetherNetService has no screen, so a connected app is
    /// where a person sees them. Empty from a host with no such permissions, and from an older service.
    /// </summary>
    public IReadOnlyList<ServicePermission> Permissions { get; init; } = System.Array.Empty<ServicePermission>();

    /// <summary>
    /// Whether AetherNet's nearby radios are switched on for this device — the switch a connected app shows as
    /// "AetherNet is on / off" (<see cref="IAetherNodeClient.SetNearbyAsync"/>). Off, only the internet leg runs. True
    /// from a host that has no such switch, and from an older service, both of which always run their radios.
    /// </summary>
    public bool NearbyOn { get; init; } = true;
}

/// <summary>One permission the phone keeps for AetherNetService.</summary>
/// <param name="Name">What the phone calls it — "Nearby devices", "Location", "Notifications", "Battery", "App launch".</param>
/// <param name="Allowed">Whether the person has allowed it.</param>
/// <param name="For">What it lets AetherNetService do, in the words of someone holding the phone, to follow "it can".</param>
public sealed record ServicePermission(string Name, bool Allowed, string For)
{
    /// <summary>Where on the phone the person changes it.</summary>
    public PermissionPage Page { get; init; } = PermissionPage.AppInfo;

    /// <summary>
    /// False when the phone does not say whether it is allowed — a phone maker's own switch, such as Huawei's App
    /// launch. <see cref="Allowed"/> is then false, meaning only "not known to be".
    /// </summary>
    public bool Known { get; init; } = true;
}

/// <summary>Where on the phone one of AetherNetService's permissions is changed.</summary>
public enum PermissionPage
{
    /// <summary>AetherNetService's own page in the phone's settings (App info), where its runtime permissions are.</summary>
    AppInfo = 0,

    /// <summary>
    /// The phone's own prompt to let AetherNetService run without its battery limits. Any app may raise it for
    /// AetherNetService, which declares that it may ask — so it needs no screen of its own for this either.
    /// </summary>
    Battery = 1,

    /// <summary>
    /// The phone maker's page for which apps the phone may start again after stopping them — Huawei's App launch. A
    /// phone short of memory stops even a foreground service, and without this nothing starts it again.
    /// </summary>
    AppLaunch = 2,
}

/// <summary>One radio the device carries, and how it is doing right now.</summary>
/// <param name="Name">Human label, e.g. "Wi-Fi Direct", "Bluetooth", "LoRa".</param>
/// <param name="Available">Whether the radio is present and usable on this device right now.</param>
/// <param name="Linked">Whether this radio has a peer on the other end right now.</param>
/// <param name="CarriesBps">Measured (or, failing that, advertised) throughput in bits per second.</param>
public sealed record RadioStatus(string Name, bool Available, bool Linked, long CarriesBps)
{
    /// <summary>
    /// Why the radio cannot be used right now, in the words of someone holding the phone — or null when
    /// it can. "Not available" alone made a phone with Wi-Fi Direct and no permission for it read as a
    /// phone with no Wi-Fi Direct.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// True when the radio is in the phone and something the person can change is stopping it — a
    /// permission, a switch in Settings. False when it can be used, or when the hardware is not there.
    /// </summary>
    public bool Fixable { get; init; }

    /// <summary>
    /// True when what stops the radio is a permission the phone keeps for AetherNetService — granted only by the
    /// person, on AetherNetService's page in the phone's settings, because the service has no screen to ask from.
    /// A connected app offers the way there; a switched-off radio is fixable too, but not by that page.
    /// </summary>
    public bool NeedsPermission { get; init; }
}
