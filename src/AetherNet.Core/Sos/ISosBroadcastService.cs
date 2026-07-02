// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;

namespace AetherNet.Sos;

/// <summary>
/// Handles SOS origination and propagation. SOS uses
/// <see cref="PacketType.SosBroadcast"/> with extended TTL
/// (<see cref="Constants.ProtocolConstants.SosTtl"/>) and maximum priority
/// (<see cref="Constants.ProtocolConstants.SosPriority"/>). Flooding is the
/// transport: every receiving node re-broadcasts until TTL is exhausted.
/// </summary>
public interface ISosBroadcastService
{
    /// <summary>Raised when a new SOS alert arrives from the mesh.</summary>
    event EventHandler<SosAlert>? SosReceived;

    /// <summary>Raised when an SOS is marked resolved either locally or by an upstream resolution packet.</summary>
    event EventHandler<Guid>? SosResolved;

    /// <summary>
    /// Raised on the ORIGINATING node when a peer acknowledges receiving one of our active SOS alerts —
    /// proof the emergency reached at least one device. Carries the responder and the running distinct count.
    /// </summary>
    event EventHandler<SosAcknowledgement>? SosAcknowledged;

    /// <summary>
    /// Originate an SOS. Floods the mesh and (if a backend client is wired up) mirrors the alert via cloud.
    /// Returns false if the rolling rate limit (<see cref="Constants.ProtocolConstants.MaxSosBroadcastsPerHour"/>) is exhausted.
    /// </summary>
    Task<bool> BroadcastSosAsync(string broadcastType, string? message, double latitude, double longitude, string? geohash = null, CancellationToken cancellationToken = default);

    /// <summary>Mark an SOS resolved locally and stop forwarding it. No-op if the id is unknown.</summary>
    Task ResolveAsync(Guid broadcastId, CancellationToken cancellationToken = default);

    /// <summary>Returns every SOS alert currently considered active on this node.</summary>
    IReadOnlyList<SosAlert> GetActiveAlerts();

    /// <summary>Pump an incoming SOS packet into the service. Dedups, raises <see cref="SosReceived"/>, re-broadcasts.</summary>
    Task HandleAsync(MeshPacket sosPacket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pump an incoming <see cref="PacketType.SosAck"/> packet into the service. On the originating
    /// node it records the responder against the matching active alert (deduping by responder UHID)
    /// and raises <see cref="SosAcknowledged"/>. No-op if the ack references an SOS this node did not
    /// originate, or one it has already resolved.
    /// </summary>
    Task HandleAckAsync(MeshPacket ackPacket, CancellationToken cancellationToken = default);
}
