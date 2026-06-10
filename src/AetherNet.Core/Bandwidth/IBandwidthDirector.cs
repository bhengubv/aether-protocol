// SPDX-License-Identifier: MIT

namespace AetherNet.Bandwidth;

/// <summary>
/// Cross-transport bandwidth synthesis and mesh gossip coordinator.
///
/// <para>
/// The director sits above individual <see cref="IBandwidthEstimator"/> instances
/// and provides two capabilities that no existing congestion-control standard
/// addresses:
/// <list type="number">
///   <item>
///     <b>Multi-transport BDP matrix.</b> AetherNet nodes may have BLE, Wi-Fi Direct,
///     NearLink, and HTTP relay transports active simultaneously. The director maintains
///     a per-peer-per-transport estimate matrix and answers "which transport should I use
///     for a 1 MB transfer to peer X?" correctly, even when the transports have wildly
///     different bandwidth profiles.
///   </item>
///   <item>
///     <b>Mesh gossip pre-warming.</b> When two nodes first handshake, the director
///     emits a <see cref="BandwidthGossipPayload"/> carrying the local node's current
///     BtlBw estimate. The receiving node's director feeds this into the appropriate
///     estimator via <see cref="IBandwidthEstimator.WarmFromGossip"/> so the new
///     session starts with a non-zero estimate. QUIC and TCP always start cold at
///     ~14.6 kB/s (RFC 6928 §2); gossip warming is unique to AetherNet.
///   </item>
/// </list>
/// </para>
/// </summary>
public interface IBandwidthDirector
{
    /// <summary>
    /// Get the bandwidth estimate for a specific peer on a specific transport.
    /// Returns null if no estimate exists yet.
    /// </summary>
    BandwidthSample? GetEstimate(string peerUhid, string transportName);

    /// <summary>
    /// Get all current estimates for a peer across all transports, ranked by
    /// <see cref="BandwidthSample.AvailableBps"/> descending.
    /// </summary>
    IReadOnlyList<BandwidthSample> GetEstimates(string peerUhid);

    /// <summary>
    /// Recommend the best transport for a payload of <paramref name="payloadBytes"/>.
    /// Takes BDP, utilization, and power cost into account. Returns null if the node
    /// has no available transports.
    /// </summary>
    string? RecommendTransport(string peerUhid, long payloadBytes);

    /// <summary>
    /// Build a gossip payload for a new peer that has just completed handshake.
    /// The payload should be included in the initial protocol exchange.
    /// </summary>
    BandwidthGossipPayload? BuildGossipPayload(string peerUhid, string transportName);

    /// <summary>
    /// Receive and apply a gossip payload from a remote peer.
    /// </summary>
    void ApplyGossip(BandwidthGossipPayload payload);

    /// <summary>
    /// Register an estimator with this director. Called once per transport at startup.
    /// </summary>
    void Register(IBandwidthEstimator estimator);
}
