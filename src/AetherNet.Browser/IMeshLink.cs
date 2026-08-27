// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// The radio, as much of it as a browser has any business knowing about.
///
/// <para>
/// A mesh browser needs four facts and one verb: is there a radio at all, is it linked to somebody
/// right now, what is it called so a reader can be told, has a packet arrived — and send this packet.
/// Everything else a real radio does is somebody else's concern, and a browser that knew about
/// channel selection or bandwidth estimates could not be lifted onto a device whose radios work
/// differently.
/// </para>
///
/// <para>
/// Deliberately one link, not a peer set. On a phone-to-phone radio there is exactly one other end,
/// so "broadcast" and "send" are the same act and a next-hop address means nothing. A host that has
/// real routing implements this over whatever it has; the browser does not need to know.
/// </para>
/// </summary>
public interface IMeshLink
{
    /// <summary>Whether this device has a radio the browser can use at all.</summary>
    /// <remarks>
    /// False on a desktop, in a test, or on a phone whose radios are off. The browser still works —
    /// your own cards are yours, and the ones you hold are still held. It simply meets nobody.
    /// </remarks>
    bool IsSupported { get; }

    /// <summary>Whether somebody is on the other end right now.</summary>
    bool IsLinked { get; }

    /// <summary>What the link is called, in words a reader would recognise — "Wi-Fi Direct", "Bluetooth".</summary>
    /// <remarks>
    /// Shown to a person, so it is a name rather than an identifier. "Fetched over Bluetooth" tells
    /// somebody why a picture is taking a moment; "BLE_GATT_1" tells them nothing.
    /// </remarks>
    string Name { get; }

    /// <summary>Ask the host to bring a link up, if it can.</summary>
    /// <remarks>
    /// Fire-and-forget on purpose. Linking is a physical act that may need a person to walk closer,
    /// turn something on, or agree to something — none of which a browser can wait on, and all of
    /// which the host is better placed to handle.
    /// </remarks>
    void Link();

    /// <summary>Raised when any of the above changes.</summary>
    event Action? Changed;

    /// <summary>Raised with the raw bytes of an arriving packet.</summary>
    /// <remarks>
    /// Bytes, not a parsed packet: the browser deserialises them itself and drops anything it cannot
    /// read. What arrives on a radio was written by a stranger, and the first thing that touches it
    /// should be code that expects that.
    /// </remarks>
    event Action<byte[]>? PacketReceived;

    /// <summary>Push these bytes to whoever is on the other end. False if nobody is, or it failed.</summary>
    Task<bool> SendAsync(byte[] packetBytes);
}
