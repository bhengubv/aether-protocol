// SPDX-License-Identifier: MIT

namespace Aether.Transport.NearLink;

/// <summary>
/// NearLink transport for HarmonyOS/OpenHarmony devices.
///
/// NearLink (formerly StarFlash) is Huawei's next-generation short-range wireless protocol
/// designed as a successor to both Bluetooth and Wi-Fi for IoT and device-to-device communication.
///
/// Key specifications:
/// <list type="bullet">
///   <item><description><b>Range:</b> Up to 600 meters (vs BLE ~100m, Wi-Fi Direct ~200m)</description></item>
///   <item><description><b>Bandwidth:</b> Up to 12 Mbps (6x BLE, suitable for voice and light streaming)</description></item>
///   <item><description><b>Latency:</b> 20 microseconds (vs BLE ~6ms, Wi-Fi ~10ms) — enables real-time voice</description></item>
///   <item><description><b>Power consumption:</b> 60% less than BLE 5.0, critical for mesh relay nodes</description></item>
///   <item><description><b>Concurrent connections:</b> 500+ peers (vs BLE ~7, Wi-Fi Direct ~8)</description></item>
///   <item><description><b>Coexistence:</b> Can operate alongside BLE and Wi-Fi without interference</description></item>
/// </list>
///
/// NearLink is the preferred transport when available, as it combines the low power of BLE
/// with the range and bandwidth approaching Wi-Fi Direct, making it ideal for mesh networking
/// in dense urban environments and emergency scenarios.
/// </summary>
public interface INearLinkTransportService
{
    /// <summary>Human-readable name: "NearLink".</summary>
    string Name => "NearLink";

    /// <summary>Whether NearLink hardware is available and enabled on this device.</summary>
    bool IsAvailable { get; }

    /// <summary>Maximum bandwidth: 12 Mbps.</summary>
    long MaxBandwidthBps => 12_000_000;

    /// <summary>Maximum range: 600 meters.</summary>
    int MaxRangeMeters => 600;

    /// <summary>
    /// Power cost relative to other transports. NearLink uses 60% less power than BLE,
    /// making it the most power-efficient transport available.
    /// </summary>
    int PowerCostRelative => 1;

    /// <summary>Maximum concurrent peer connections: 500+.</summary>
    int MaxConcurrentPeers => 500;

    /// <summary>Number of peers currently connected via NearLink.</summary>
    int ConnectedPeerCount { get; }

    /// <summary>
    /// Sends raw data to a specific peer over NearLink.
    /// Benefits from NearLink's 20μs latency for real-time applications.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the target peer.</param>
    /// <param name="data">The data to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if sent successfully.</returns>
    Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a stream of data to a specific peer over NearLink.
    /// With 12 Mbps bandwidth, NearLink can handle voice and light video streaming.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the target peer.</param>
    /// <param name="stream">The data stream to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the stream was sent successfully.</returns>
    Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a specific peer is connected via NearLink.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the peer.</param>
    /// <returns>True if the peer is connected.</returns>
    bool IsConnected(string peerUhid);

    /// <summary>
    /// Raised when data is received from a peer over NearLink.
    /// First argument: sender UHID. Second argument: raw data.
    /// </summary>
    event Action<string, byte[]>? DataReceived;

    /// <summary>
    /// Raised when a new peer connects via NearLink.
    /// The argument is the connected peer's UHID.
    /// </summary>
    event Action<string>? PeerConnected;

    /// <summary>
    /// Raised when a peer disconnects from the NearLink session.
    /// The argument is the disconnected peer's UHID.
    /// </summary>
    event Action<string>? PeerDisconnected;
}
