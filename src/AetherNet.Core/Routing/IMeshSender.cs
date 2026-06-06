// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;

namespace AetherNet.Routing;

/// <summary>
/// Minimal sending abstraction routing, DTN, and SOS need. Lets these services
/// live in <c>AetherNet.Core</c> without taking a hard dependency on <c>AetherNet.Transport</c>;
/// hosts wire this up with a thin adapter over their transport manager.
/// </summary>
public interface IMeshSender
{
    /// <summary>The local node's UHID. Used as <see cref="MeshPacket.SourceUhid"/> on outbound packets.</summary>
    string LocalUhid { get; }

    /// <summary>The local node's last known geohash, or null if location is not shared. Used by DTN replication.</summary>
    string? LocalGeohash => null;

    /// <summary>Snapshot of peers the local node is currently connected to. Used by DTN replication and SOS broadcast.</summary>
    IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();

    /// <summary>
    /// Forward a packet to a single next-hop peer (already routed). Used for RREP forwarding
    /// when the next hop is known from the reverse route installed by the matching RREQ.
    /// </summary>
    /// <returns>True if delivered to <paramref name="nextHopUhid"/>; false otherwise.</returns>
    Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast a packet to every directly connected peer. Used for RREQ flooding.
    /// </summary>
    /// <returns>The number of peers the packet was delivered to (0 means the broadcast was a no-op).</returns>
    Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}
