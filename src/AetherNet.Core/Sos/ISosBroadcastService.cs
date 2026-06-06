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
}
