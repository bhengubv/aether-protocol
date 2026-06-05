// SPDX-License-Identifier: MIT

using AetherMesh.Protocol;

namespace AetherMesh.Reputation;

/// <summary>
/// Signs, broadcasts, and handles <see cref="PacketType.ReputationUpdate"/> gossip packets.
///
/// Gossip flow:
/// <list type="number">
///   <item>Caller invokes <see cref="BroadcastReputationUpdateAsync"/> when a local event
///     (sig failure, replay, flood, etc.) warrants informing peers.</item>
///   <item>The service serialises a <see cref="ReputationUpdatePayload"/>, wraps it in a
///     signed <see cref="MeshPacket"/> and broadcasts to all connected peers.</item>
///   <item>Peers receive the packet and call <see cref="HandleGossipPacketAsync"/>.</item>
///   <item>The handler verifies the enclosing packet signature, checks freshness, then
///     fetches the reporter's local reputation score R and applies
///     <c>effective_delta = ScoreDelta × R</c> to <see cref="INodeReputationService"/>.</item>
/// </list>
/// </summary>
public interface IReputationGossipService
{
    /// <summary>
    /// Build and broadcast a signed <see cref="PacketType.ReputationUpdate"/> packet to
    /// all currently connected peers. The <paramref name="scoreDelta"/> MUST be in [−1, +1];
    /// values outside that range are clamped before broadcast.
    /// </summary>
    /// <param name="targetUhid">UHID of the node whose reputation is being reported.</param>
    /// <param name="scoreDelta">Raw score delta claimed by the local node (reporter).
    ///   Negative = degradation; positive = improvement.</param>
    /// <param name="reason">Human-readable reason code for auditing, e.g. "sig_failure".</param>
    /// <param name="ct">Cancellation token.</param>
    Task BroadcastReputationUpdateAsync(
        string targetUhid,
        double scoreDelta,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Process an inbound <see cref="PacketType.ReputationUpdate"/> packet received from
    /// a peer. Implementations MUST:
    /// <list type="number">
    ///   <item>Verify the packet signature via <c>IPacketSigningService</c> using the
    ///     <paramref name="senderPublicKey"/>.</item>
    ///   <item>Check <c>TimestampMs</c> freshness (reject if &gt; 5 min old).</item>
    ///   <item>Deserialise the <see cref="ReputationUpdatePayload"/>.</item>
    ///   <item>Fetch reporter's local reputation R from <c>INodeReputationService</c>.</item>
    ///   <item>Apply <c>effective_delta = payload.ScoreDelta × R</c> to the target's score.</item>
    /// </list>
    /// </summary>
    /// <param name="packet">The received <see cref="PacketType.ReputationUpdate"/> packet.</param>
    /// <param name="senderPublicKey">Ed25519 public key of the sending peer, used for signature
    ///   verification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///   <see langword="true"/> if the gossip was accepted and applied;
    ///   <see langword="false"/> if it was rejected (bad signature, stale, malformed payload).
    /// </returns>
    Task<bool> HandleGossipPacketAsync(
        MeshPacket packet,
        byte[] senderPublicKey,
        CancellationToken ct = default);
}
