// SPDX-License-Identifier: MIT

namespace AetherNet.Node;

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
}

/// <summary>One radio the device carries, and how it is doing right now.</summary>
/// <param name="Name">Human label, e.g. "Wi-Fi Direct", "Bluetooth", "LoRa".</param>
/// <param name="Available">Whether the radio is present and usable on this device right now.</param>
/// <param name="Linked">Whether this radio has a peer on the other end right now.</param>
/// <param name="CarriesBps">Measured (or, failing that, advertised) throughput in bits per second.</param>
public sealed record RadioStatus(string Name, bool Available, bool Linked, long CarriesBps);
