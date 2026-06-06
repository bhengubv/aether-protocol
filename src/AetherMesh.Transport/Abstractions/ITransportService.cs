// SPDX-License-Identifier: MIT

using AetherMesh.Protocol;
using AetherMesh.Transport.Models;

namespace AetherMesh.Transport.Abstractions;

/// <summary>
/// Generic transport interface that all mesh networking backends must implement.
/// Each transport (BLE, Wi-Fi Direct, NearLink, etc.) provides a concrete implementation
/// that handles the physical-layer details of sending and receiving <see cref="MeshPacket"/> data.
/// </summary>
public interface ITransportService
{
    /// <summary>Human-readable name of this transport (e.g. "BLE", "Wi-Fi Direct", "NearLink").</summary>
    string Name { get; }

    /// <summary>Whether this transport is currently available on the device.</summary>
    bool IsAvailable { get; }

    /// <summary>Maximum theoretical bandwidth in bits per second.</summary>
    long MaxBandwidthBps { get; }

    /// <summary>Maximum effective range in meters. 0 means unlimited or not applicable.</summary>
    int MaxRangeMeters { get; }

    /// <summary>
    /// Relative power cost of using this transport (0 = free, higher = more expensive).
    /// Used by <see cref="AetherMesh.Transport.Services.TransportManager"/> to prefer lower-cost transports.
    /// </summary>
    int PowerCostRelative { get; }

    /// <summary>Maximum number of peers this transport can handle concurrently.</summary>
    int MaxConcurrentPeers { get; }

    /// <summary>
    /// Live per-transport EWMA metrics (RTT, loss rate, throughput). Null if this
    /// transport has not yet been registered with a <c>PredictiveTransportSelector</c>.
    /// </summary>
    PerTransportMetrics? Metrics => null;

    /// <summary>
    /// Sends raw data to a specific peer identified by their UHID.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the target peer.</param>
    /// <param name="data">The data to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the data was sent successfully; false otherwise.</returns>
    Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a stream of data to a specific peer. Used for large payloads (file transfers, voice, streaming).
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the target peer.</param>
    /// <param name="stream">The data stream to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the stream was sent successfully; false otherwise.</returns>
    Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a specific peer is currently connected via this transport.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the peer to check.</param>
    /// <returns>True if the peer is connected; false otherwise.</returns>
    bool IsConnected(string peerUhid);

    /// <summary>
    /// Raised when data is received from any peer over this transport.
    /// The first argument is the sender's UHID, the second is the raw data.
    /// </summary>
    event Action<string, byte[]>? DataReceived;
}
