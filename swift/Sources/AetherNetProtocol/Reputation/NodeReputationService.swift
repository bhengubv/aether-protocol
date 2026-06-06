// SPDX-License-Identifier: MIT

import Foundation

/// Aggregates per-UHID behavioral signals into a reputation score in [0.0, 1.0].
///
/// Score semantics:
///   1.0 = pristine — no negative signals observed.
///   0.5 = degraded — recurring minor violations or unreliable delivery.
///   0.0 = untrusted — active attacker or catastrophic failure rate.
///
/// Signal weights (additive deltas, clamped to [0, 1]):
///   RREQ flood attempt   : −0.05
///   Replay attack         : −0.15
///   Signature failure     : −0.20
///   Custody refusal       : −0.05
///   Delivery failure      : −0.02
///   Delivery success      : +0.01
///
/// Unknown peers default to 1.0 (benefit of the doubt).
///
/// Thread-safety: implemented as a Swift `actor`; all mutations are serialised
/// on the actor's executor — no locks required.
public actor NodeReputationService {

    // MARK: - Score deltas (match C# InMemoryNodeReputationService exactly)

    private static let deltaRreqFlood:      Double = -0.05
    private static let deltaReplay:         Double = -0.15
    private static let deltaSigFailure:     Double = -0.20
    private static let deltaCustodyRefusal: Double = -0.05
    private static let deltaDeliveryFail:   Double = -0.02
    private static let deltaDeliveryOk:     Double = +0.01

    // MARK: - Storage

    private var scores: [String: Double] = [:]

    // MARK: - Public API

    /// Record that a RREQ was rate-limited from `uhid`.
    public func recordRreqFloodAttempt(uhid: String) {
        applyDelta(to: uhid, delta: Self.deltaRreqFlood)
    }

    /// Record a duplicate-nonce replay from `uhid`.
    public func recordReplayAttempt(uhid: String) {
        applyDelta(to: uhid, delta: Self.deltaReplay)
    }

    /// Record an Ed25519 signature verification failure from `uhid`.
    public func recordSignatureFailure(uhid: String) {
        applyDelta(to: uhid, delta: Self.deltaSigFailure)
    }

    /// Record a DTN custody refusal by `uhid`.
    public func recordCustodyRefusal(uhid: String) {
        applyDelta(to: uhid, delta: Self.deltaCustodyRefusal)
    }

    /// Record a confirmed successful delivery via `uhid`.
    public func recordDeliverySuccess(uhid: String, roundTripMs: Int) {
        applyDelta(to: uhid, delta: Self.deltaDeliveryOk)
    }

    /// Record a delivery failure (lost bundle / unacknowledged hop) through `uhid`.
    public func recordDeliveryFailure(uhid: String) {
        applyDelta(to: uhid, delta: Self.deltaDeliveryFail)
    }

    /// Returns the current reputation score for `uhid` in [0.0, 1.0].
    /// Returns 1.0 for unknown peers (benefit of the doubt).
    public func reputationScore(for uhid: String) -> Double {
        scores[uhid] ?? 1.0
    }

    /// Returns a snapshot copy of all known reputation scores.
    public func allScores() -> [String: Double] {
        scores
    }

    /// Apply a pre-weighted delta (positive or negative) to `uhid`.
    ///
    /// Used by ``ReputationGossipService`` to apply gossip-sourced score
    /// adjustments that have already been scaled by the reporter's own
    /// reputation weight before being passed here. The delta is not further
    /// clamped before application; the result is clamped to [0, 1] as usual.
    public func applyWeightedDelta(uhid: String, weightedDelta: Double) {
        applyDelta(to: uhid, delta: weightedDelta)
    }

    // MARK: - Private helpers

    /// Clamp `v` to [0.0, 1.0] with epsilon snap matching the C# implementation.
    ///
    /// If the clamped result < 1e-12 it is snapped to exactly 0.0;
    /// if > 1.0 − 1e-12 it is snapped to exactly 1.0.
    private func clampScore(_ v: Double) -> Double {
        let clamped = max(0.0, min(1.0, v))
        if clamped < 1e-12         { return 0.0 }
        if clamped > 1.0 - 1e-12  { return 1.0 }
        return clamped
    }

    /// Applies `delta` to the stored score for `uhid`.
    /// New peers start at 1.0 before the delta is applied, mirroring the
    /// C# `AddOrUpdate` with `_ => ClampScore(1.0 + delta)` seed.
    private func applyDelta(to uhid: String, delta: Double) {
        let current = scores[uhid] ?? 1.0
        scores[uhid] = clampScore(current + delta)
    }
}
