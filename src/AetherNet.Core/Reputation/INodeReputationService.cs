// SPDX-License-Identifier: MIT

namespace AetherNet.Reputation;

/// <summary>
/// Aggregates per-UHID behavioral signals into a single reputation score in [0, 1].
///
/// Score semantics:
///   1.0 = pristine — no negative signals observed.
///   0.5 = degraded — recurring minor violations or unreliable delivery.
///   0.0 = untrusted — active attacker or catastrophic failure rate.
///
/// Signal weights (applied as additive score deltas, clamped to [0, 1]):
///   RREQ flood attempt   : −0.05  (exceeds per-source rate limit)
///   Replay attack         : −0.15  (duplicate nonce from the same source)
///   Signature failure     : −0.20  (Ed25519 verify failed)
///   Custody refusal       : −0.05  (DTN bundle rejected above expected rate)
///   Delivery success      : +0.01  (confirmed end-to-end delivery, EWMA smoothed)
///   Delivery failure      : −0.02  (unacknowledged hop / lost bundle)
/// </summary>
public interface INodeReputationService
{
    /// <summary>Record that a RREQ was rate-limited from <paramref name="uhid"/>.</summary>
    Task RecordRreqFloodAttemptAsync(string uhid, CancellationToken ct = default);

    /// <summary>Record a duplicate-nonce replay from <paramref name="uhid"/>.</summary>
    Task RecordReplayAttemptAsync(string uhid, CancellationToken ct = default);

    /// <summary>Record an Ed25519 signature verification failure from <paramref name="uhid"/>.</summary>
    Task RecordSignatureFailureAsync(string uhid, CancellationToken ct = default);

    /// <summary>Record a DTN custody refusal by <paramref name="uhid"/>.</summary>
    Task RecordCustodyRefusalAsync(string uhid, CancellationToken ct = default);

    /// <summary>
    /// Record a confirmed successful delivery via <paramref name="uhid"/> with the
    /// observed round-trip time. Positive signal: lifts score slightly.
    /// </summary>
    Task RecordDeliverySuccessAsync(string uhid, int roundTripMs, CancellationToken ct = default);

    /// <summary>
    /// Record a delivery failure (lost bundle / unacknowledged hop) through
    /// <paramref name="uhid"/>.
    /// </summary>
    Task RecordDeliveryFailureAsync(string uhid, CancellationToken ct = default);

    /// <summary>
    /// Returns the current reputation score for <paramref name="uhid"/> in [0, 1].
    /// Returns 1.0 for unknown peers (benefit of the doubt until signals arrive).
    /// </summary>
    Task<double> GetReputationScoreAsync(string uhid, CancellationToken ct = default);

    /// <summary>
    /// The weight this node's gossiped REPORTS carry — earned, not granted. Returns 0 for an
    /// unknown reporter (one we hold no first-hand record of), else their standing score. This is
    /// what defeats sybil brigading: a swarm of fresh identities we have never interacted with
    /// carries zero aggregate weight, so it cannot move a victim's score. Distinct from
    /// <see cref="GetReputationScoreAsync"/>, whose 1.0 innocent-until-proven default must never
    /// leak into gossip weight.
    /// </summary>
    Task<double> GetGossipWeightAsync(string uhid, CancellationToken ct = default);

    /// <summary>Returns a snapshot of all known reputation scores.</summary>
    Task<IReadOnlyDictionary<string, double>> GetAllScoresAsync(CancellationToken ct = default);

    /// <summary>
    /// Apply a pre-weighted score delta directly. Used by gossip propagation:
    /// the caller has already scaled the raw claimed delta by the reporter's
    /// reputation score so the weighting is applied exactly once.
    /// <paramref name="weightedDelta"/> is clamped to [−1, +1] before application.
    /// </summary>
    Task ApplyWeightedDeltaAsync(string uhid, double weightedDelta, CancellationToken ct = default);
}
