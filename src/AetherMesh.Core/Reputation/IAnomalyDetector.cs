// SPDX-License-Identifier: MIT

namespace AetherMesh.Reputation;

/// <summary>
/// Observes raw behavioral signals from the mesh and detects anomalous patterns.
/// When a pattern exceeds its threshold, the detector emits a reputation signal via
/// <see cref="INodeReputationService"/>.
///
/// Detectable patterns and thresholds:
/// <list type="bullet">
///   <item><term>Volume spike</term>
///     <description>A node's per-30s packet rate exceeds 5× its EWMA baseline (α=0.20).
///     Emits: <see cref="INodeReputationService.RecordRreqFloodAttemptAsync"/>.
///     </description></item>
///   <item><term>Destination scatter</term>
///     <description>A single source sends to more than 50 unique destinations within
///     a 60-second sliding window (port-scan / address-scan behaviour).
///     Emits: <see cref="INodeReputationService.RecordRreqFloodAttemptAsync"/>.
///     </description></item>
///   <item><term>Geohash mismatch</term>
///     <description>A node claims geohash X but observed routing paths consistently
///     originate from a geohash prefix that differs at the 4-char level (~50 km).
///     Emits: <see cref="INodeReputationService.RecordSignatureFailureAsync"/> (identity
///     spoofing indicator, same severity as key-confusion attack).
///     </description></item>
///   <item><term>Repeated SPK-sig failures</term>
///     <description>A node attempts session initiation with an invalid signed pre-key
///     (key confusion or active probing).
///     Emits: <see cref="INodeReputationService.RecordSignatureFailureAsync"/>.
///     </description></item>
/// </list>
/// </summary>
public interface IAnomalyDetector
{
    /// <summary>
    /// Called for every successfully parsed packet. Updates volume and destination-
    /// scatter windows for <paramref name="sourceUhid"/>.
    /// </summary>
    /// <param name="sourceUhid">UHID of the sending node.</param>
    /// <param name="destinationUhid">UHID of the packet's destination.</param>
    /// <param name="timestampMs">
    /// Unix timestamp in milliseconds (pass <c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()</c>).
    /// Allows unit-testing without a real clock.
    /// </param>
    void ObservePacket(string sourceUhid, string destinationUhid, long timestampMs);

    /// <summary>
    /// Called when the routing layer observes a geohash on the actual path that differs
    /// from the geohash the source node has advertised. A single mismatch can be noise
    /// (node is mobile); consistent mismatches across multiple packets indicate spoofing.
    /// </summary>
    /// <param name="uhid">The node whose claimed geohash is suspect.</param>
    /// <param name="claimedGeohash">The geohash the node advertised in its packet header.</param>
    /// <param name="observedRoutingGeohash">The geohash implied by the observed routing path.</param>
    void ObserveGeohashClaim(string uhid, string claimedGeohash, string observedRoutingGeohash);

    /// <summary>
    /// Called when an X3DH session initiation is rejected because the signed pre-key
    /// signature is invalid (Ed25519 verify failed on the SPK). Repeated failures from the
    /// same UHID indicate active probing or key confusion.
    /// </summary>
    void ObserveSpkSigFailure(string uhid);
}
