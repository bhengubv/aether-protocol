// SPDX-License-Identifier: MIT

namespace AetherNet.Transport.Services;

/// <summary>
/// The names transports go by when two phones tell each other what they can carry.
///
/// <para>
/// The capability handshake already exchanges a set of string tags and hands back the intersection —
/// the things BOTH ends can do. Transports are exactly that kind of thing: a phone with Wi-Fi Aware
/// silicon can carry over it, one without cannot, and there is no point either of them preferring a
/// radio the other can never hear. So a phone adds a tag here to the capabilities it advertises, the
/// handshake intersects them like any other, and what comes back is the transports the two of them
/// share — which is what <see cref="RadioChoice.Order(System.Collections.Generic.IEnumerable{RadioSpeed}, string?, System.Collections.Generic.IReadOnlySet{string}?)"/>
/// then prefers.
/// </para>
///
/// <para>
/// The tags are wire constants, matched ordinally, so they live in one place both the side that
/// advertises them and the side that ranks against them can read. They are namespaced with
/// <see cref="Prefix"/> so a transport tag never collides with a protocol-feature tag
/// (<c>signal-x3dh</c>, <c>voice</c>, …) in the same set, and so a reader can pick the transport tags
/// back out of a negotiated capability set with <see cref="IsTransport"/>.
/// </para>
/// </summary>
public static class TransportCapability
{
    /// <summary>Namespace every transport tag carries, so it never collides with a feature tag.</summary>
    public const string Prefix = "transport:";

    /// <summary>Wi-Fi Direct — the universal floor: every tested phone has it and it carries real traffic.</summary>
    public const string WifiDirect = Prefix + "wifi-direct";

    /// <summary>Wi-Fi Aware (NAN) — no group, no owner, continuous discovery; only newer silicon has it.</summary>
    public const string WifiAware = Prefix + "wifi-aware";

    /// <summary>Bluetooth LE — always there, slow; the radio that carries when nothing else can.</summary>
    public const string Ble = Prefix + "ble";

    /// <summary>The infrastructure Wi-Fi both phones already sit on.</summary>
    public const string Wifi = Prefix + "wifi";

    /// <summary>The internet leg — a phone in the Circle relaying, when nobody is in range.</summary>
    public const string Internet = Prefix + "internet";

    /// <summary>Cellular data.</summary>
    public const string MobileData = Prefix + "mobile-data";

    /// <summary>LoRa — kilometres of range at a few hundred bits per second, over a USB module.</summary>
    public const string Lora = Prefix + "lora";

    /// <summary>
    /// The tag for a radio's display name, or null when the radio maps to no advertised transport
    /// (NFC is a way to meet, not a way to carry, so it has none).
    /// </summary>
    /// <remarks>
    /// Keyed on the names the radios call themselves so the one place a name is turned into a tag is
    /// here, and both the advertising side and the ranking side get the same answer. An unknown name
    /// returns null rather than an invented tag: a transport nobody can name is one no peer can match.
    /// </remarks>
    public static string? TagFor(string? radioName) => radioName switch
    {
        "Wi-Fi Direct" => WifiDirect,
        "Wi-Fi Aware" => WifiAware,
        "BLE" => Ble,
        "Bluetooth" => Ble,
        "Wi-Fi" => Wifi,
        "Internet" => Internet,
        "Mobile data" => MobileData,
        "LoRa" => Lora,
        _ => null,
    };

    /// <summary>Whether a capability tag names a transport rather than a protocol feature.</summary>
    public static bool IsTransport(string? capability) =>
        capability is not null && capability.StartsWith(Prefix, StringComparison.Ordinal);
}
