// SPDX-License-Identifier: MIT

using AetherMesh.Transport.Models;

namespace AetherMesh.Transport.Abstractions;

/// <summary>
/// Manages multiple transport backends and routes data through the best available transport.
/// The manager selects transports based on payload size, availability, power cost,
/// and priority order (NearLink → BLE → Wi-Fi Direct → additional transports).
/// </summary>
public interface ITransportManager
{
    /// <summary>
    /// Sends data to a peer using the best available transport.
    /// Transport selection considers payload size, peer connectivity, and power cost.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the target peer.</param>
    /// <param name="data">The data to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the data was sent successfully via any transport; false if no transport could deliver.</returns>
    Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a stream to a peer using the best available stream-capable transport.
    /// </summary>
    /// <param name="peerUhid">The Universal Hash ID of the target peer.</param>
    /// <param name="stream">The data stream to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the stream was sent successfully; false if no transport could deliver.</returns>
    Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns cumulative transport metrics across all backends.
    /// </summary>
    TransportMetrics GetMetrics();

    /// <summary>
    /// Raised when data is received from any peer on any transport.
    /// First argument: sender UHID. Second argument: raw data. Third argument: transport name.
    /// </summary>
    event Action<string, byte[], string>? DataReceived;
}
