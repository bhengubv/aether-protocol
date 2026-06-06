// SPDX-License-Identifier: MIT

namespace AetherMesh.Transport.Abstractions;

/// <summary>
/// Extension point for custom or community-contributed transport backends.
/// Implementors can register additional transports (e.g. LoRa, satellite, infrared)
/// that plug into the <see cref="ITransportManager"/> selection pipeline.
/// </summary>
public interface ICircleLinkTransportService : ITransportService
{
    /// <summary>
    /// Raised when a peer connects via this transport.
    /// </summary>
    event Action<string>? PeerConnected;

    /// <summary>
    /// Raised when a peer disconnects from this transport.
    /// </summary>
    event Action<string>? PeerDisconnected;
}
