// SPDX-License-Identifier: MIT

namespace AetherMesh.Transport.Abstractions;

/// <summary>
/// Wi-Fi Direct transport for high-bandwidth peer-to-peer communication.
/// Wi-Fi Direct provides significantly higher throughput than BLE (~250 Mbps)
/// and is preferred for large payloads, file transfers, voice calls, and streaming.
/// Typical range: up to 200m with clear line of sight.
/// </summary>
public interface IWifiDirectService : ITransportService
{
    /// <summary>
    /// Initiates a Wi-Fi Direct connection to a specific peer.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the peer to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the connection was established successfully.</returns>
    Task<bool> ConnectAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from a specific peer.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the peer to disconnect from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DisconnectAsync(string peerUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a peer connects via Wi-Fi Direct.
    /// The argument is the connected peer's UHID.
    /// </summary>
    event Action<string>? PeerConnected;

    /// <summary>
    /// Raised when a peer disconnects from the Wi-Fi Direct session.
    /// The argument is the disconnected peer's UHID.
    /// </summary>
    event Action<string>? PeerDisconnected;
}
