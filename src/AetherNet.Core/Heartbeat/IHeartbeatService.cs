// SPDX-License-Identifier: MIT

using AetherNet.Protocol;

namespace AetherNet.Heartbeat;

/// <summary>
/// Broadcasts and handles <see cref="PacketType.Heartbeat"/> liveness beacons. A node periodically
/// emits a heartbeat to its direct neighbours (TTL 1); receivers maintain a per-peer
/// <see cref="PeerLiveness"/> table and can query which peers are currently live.
/// </summary>
public interface IHeartbeatService
{
    /// <summary>Raised when a heartbeat is received from a peer (new or refreshed liveness).</summary>
    event EventHandler<PeerLiveness>? PeerSeen;

    /// <summary>
    /// Broadcast a single heartbeat to all directly connected peers (TTL 1). The sequence number
    /// increments on every call. Returns the number of peers the beacon was delivered to.
    /// </summary>
    Task<int> SendHeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Process an incoming <see cref="PacketType.Heartbeat"/> packet: refresh the sender's liveness
    /// record and raise <see cref="PeerSeen"/>. No-op (returns false) for self-originated heartbeats,
    /// the wrong packet type, or a malformed payload.
    /// </summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>Snapshot of every peer this node has ever seen a heartbeat from.</summary>
    IReadOnlyList<PeerLiveness> GetKnownPeers();

    /// <summary>Peers whose most recent heartbeat was received within the last <paramref name="withinSeconds"/> seconds.</summary>
    IReadOnlyList<PeerLiveness> GetLivePeers(int withinSeconds);
}
