// SPDX-License-Identifier: MIT

using Aether.Transport.Abstractions;

namespace Aether.Transport.CircleLink;

/// <summary>
/// LoRa (Long Range) stub implementing the <see cref="ICircleLinkTransportService"/> seam.
/// LoRa is the Aether Red transport — up to 15 km range at low data rates.
///
/// <b>HARDWARE BLOCKED:</b> Requires a physical LoRa radio module (e.g. Semtech SX1276,
/// Heltec WiFi LoRa 32, RAK WisBlock) connected via USB-to-serial or SPI.
/// <see cref="IsAvailable"/> is <see langword="false"/> until hardware is present.
///
/// <h3>To implement a real driver:</h3>
/// <list type="number">
///   <item><description>Connect a LoRa module via USB-serial or SPI.</description></item>
///   <item><description>Replace this stub with a class that implements <see cref="ICircleLinkTransportService"/>.</description></item>
///   <item><description>Use the AT-command or SPI driver to send/receive Aether wire-format packets.</description></item>
///   <item><description>Respect the <see cref="PowerCostRelative"/> ordering in <c>TransportManager</c>.</description></item>
/// </list>
/// </summary>
public sealed class LoRaCircleLinkStub : ICircleLinkTransportService
{
    /// <inheritdoc />
    public string Name => "Aether Red (LoRa/CircleLink)";

    /// <inheritdoc />
    public bool IsAvailable => false; // Requires physical LoRa radio hardware module

    /// <inheritdoc />
    public long MaxBandwidthBps => 37_500; // LoRa SF7 BW125 kHz ≈ 37.5 kbps

    /// <inheritdoc />
    public int MaxRangeMeters => 15_000; // Up to 15 km line-of-sight

    /// <inheritdoc />
    public int PowerCostRelative => 50; // High transmission power; selected only when closer transports fail

    /// <inheritdoc />
    public int MaxConcurrentPeers => 255;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event Action<string>? PeerConnected
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event Action<string>? PeerDisconnected
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "LoRa/CircleLink (Aether Red) requires a physical LoRa radio module. " +
            "Connect a LoRa32 or equivalent device and implement the hardware driver. " +
            "IsAvailable = false.");

    /// <inheritdoc />
    public Task<bool> SendStreamAsync(string peerUhid, Stream stream,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "LoRa/CircleLink (Aether Red) requires a physical LoRa radio module.");

    /// <inheritdoc />
    public bool IsConnected(string peerUhid) => false;
}
