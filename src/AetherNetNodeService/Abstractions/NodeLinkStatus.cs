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
/// <param name="Name">What the phone calls it on AetherNetService's page — "Nearby devices", "Location", "Notifications".</param>
/// <param name="Allowed">Whether the person has allowed it.</param>
/// <param name="For">What it lets AetherNetService do, in the words of someone holding the phone, to follow "it can".</param>
public sealed record ServicePermission(string Name, bool Allowed, string For);

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
