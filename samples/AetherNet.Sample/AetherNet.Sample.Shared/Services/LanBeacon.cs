// SPDX-License-Identifier: MIT

using System.Globalization;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What a phone says on the local network so the people who already know it can find it.
///
/// <para>
/// The whole of LAN discovery is this one line of text, broadcast a few times a minute: a rotating
/// wire address and the TCP port that address is listening on. There is nothing else to negotiate,
/// because on a network both phones are already on there is nothing else to arrange — no group, no
/// owner, no credentials.
/// </para>
///
/// <para>
/// It lives here rather than beside the Android radio for two reasons. It is the only part of the LAN
/// leg with no platform in it, so it is the only part that can be tested off a phone — and a
/// malformed beacon does not fail loudly, it produces a radio that runs perfectly and never finds
/// anybody. And a second head speaking this has to compose byte-identical text, so the format needs
/// one home rather than one per platform.
/// </para>
/// </summary>
public static class LanBeacon
{
    /// <summary>
    /// The version marker every beacon starts with.
    /// </summary>
    /// <remarks>
    /// Versioned from the first line so a later shape can be added without a phone having to guess
    /// which it is holding. Anything that does not start with this is somebody else's traffic on a
    /// shared port and is ignored rather than parsed hopefully.
    /// </remarks>
    public const string Prefix = "AETHER-LAN/1 ";

    /// <summary>
    /// The UDP port beacons are sent to and heard on.
    /// </summary>
    /// <remarks>
    /// Fixed, and it has to be: a broadcast nobody knows the port of is a broadcast nobody hears, so
    /// discovery cannot use an OS-assigned port the way the TCP side does. The TCP port is carried in
    /// the beacon precisely so that the one port which must be agreed in advance is the only one.
    /// </remarks>
    public const int Port = 47653;

    /// <summary>
    /// The longest a beacon may be. Anything longer is not one.
    /// </summary>
    /// <remarks>
    /// A wire address is 16 characters and a port is at most 5, so a real beacon is around 35 bytes.
    /// The ceiling exists because this reads from a socket any application on the network can write
    /// to.
    /// </remarks>
    public const int MaxLength = 96;

    /// <summary>
    /// Compose this phone's beacon.
    /// </summary>
    /// <param name="address">
    ///   The rotating wire address for the current epoch — never the AetherTag. This goes out in
    ///   clear to every device on the network, and that is safe only because it is ephemeral: an
    ///   observer sees sixteen opaque characters that change every fifteen minutes with no linkage
    ///   between windows, while a contact holding the routing key resolves it to a person. The tag
    ///   here instead would hand everyone on the café Wi-Fi a permanent name for this phone.
    /// </param>
    /// <param name="tcpPort">The port this phone's link server is accepting on.</param>
    public static string Compose(string address, int tcpPort)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("A beacon needs an address.", nameof(address));
        if (address.Contains(' ', StringComparison.Ordinal))
            throw new ArgumentException("A wire address cannot contain a space — the format splits on it.", nameof(address));
        if (tcpPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(tcpPort), tcpPort, "Not a port.");

        return string.Concat(Prefix, address, " ", tcpPort.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Read a beacon, or say plainly that this was not one.
    /// </summary>
    /// <remarks>
    /// Everything arriving on a broadcast port is untrusted: other applications share it, and anybody
    /// on the network can send whatever they like to it. So this rejects rather than salvages — a
    /// half-understood beacon would produce a dial to a port somebody else chose.
    /// </remarks>
    public static bool TryParse(string? text, out string address, out int tcpPort)
    {
        address = string.Empty;
        tcpPort = 0;

        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length > MaxLength) return false;
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var body = text[Prefix.Length..];
        var space = body.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0) return false;

        var candidate = body[..space];
        var portText = body[(space + 1)..];

        // A trailing newline or a datagram padded with nulls is somebody's else's habit, not an error.
        portText = portText.TrimEnd('\r', '\n', '\0', ' ');

        if (portText.Length == 0) return false;
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return false;
        if (port is <= 0 or > 65535) return false;

        address = candidate;
        tcpPort = port;
        return true;
    }

    /// <summary>
    /// Which of two phones opens the socket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both phones broadcast and both hear each other, so without a rule both dial and the pair ends
    /// up holding two links where one was wanted — the second silently replacing the first in the
    /// peer table while its pump carries on writing to a socket nothing reads.
    /// </para>
    /// <para>
    /// The higher address dials. It is arbitrary, it needs no exchange to agree, and it is the same
    /// convention Wi-Fi Direct already uses to decide who calls connect(). Equal addresses cannot be
    /// two phones — that is this phone hearing its own broadcast come back — so nobody dials.
    /// </para>
    /// </remarks>
    public static bool ShouldDial(string? mine, string? theirs)
    {
        if (string.IsNullOrEmpty(mine) || string.IsNullOrEmpty(theirs)) return false;
        return string.CompareOrdinal(mine, theirs) > 0;
    }
}
