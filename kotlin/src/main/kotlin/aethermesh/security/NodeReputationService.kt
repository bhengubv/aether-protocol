// SPDX-License-Identifier: MIT

package aethermesh.security

import java.util.concurrent.ConcurrentHashMap

/**
 * Thread-safe, in-memory aggregation of per-UHID behavioural signals into a
 * reputation score in [0.0, 1.0].
 *
 * Score semantics:
 *   1.0 = pristine — no negative signals observed.
 *   0.5 = degraded — recurring minor violations or unreliable delivery.
 *   0.0 = untrusted — active attacker or catastrophic failure rate.
 *
 * Signal weights (additive deltas, clamped to [0, 1]):
 *   RREQ flood attempt   : −0.05
 *   Replay attack         : −0.15
 *   Signature failure     : −0.20
 *   Custody refusal       : −0.05
 *   Delivery failure      : −0.02
 *   Delivery success      : +0.01
 *
 * Unknown peers default to 1.0 (benefit of the doubt until signals arrive).
 * Epsilon-snap: values within 1e-12 of 0 or 1 are snapped to those exact
 * values to avoid floating-point drift (e.g. 5.5e-17 instead of 0.0).
 *
 * Matches [InMemoryNodeReputationService] in the C# reference implementation.
 */
open class NodeReputationService {

    // Score deltas — negative signals
    private companion object {
        const val DELTA_RREQ_FLOOD       = -0.05
        const val DELTA_REPLAY           = -0.15
        const val DELTA_SIG_FAILURE      = -0.20
        const val DELTA_CUSTODY_REFUSAL  = -0.05
        const val DELTA_DELIVERY_FAIL    = -0.02

        // Score deltas — positive signals
        const val DELTA_DELIVERY_OK      = +0.01
    }

    private val scores = ConcurrentHashMap<String, Double>()

    /** Record that a RREQ was rate-limited from [uhid]. */
    fun recordRreqFloodAttempt(uhid: String) {
        applyDelta(uhid, DELTA_RREQ_FLOOD)
    }

    /** Record a duplicate-nonce replay from [uhid]. */
    fun recordReplayAttempt(uhid: String) {
        applyDelta(uhid, DELTA_REPLAY)
    }

    /** Record an Ed25519 signature verification failure from [uhid]. */
    fun recordSignatureFailure(uhid: String) {
        applyDelta(uhid, DELTA_SIG_FAILURE)
    }

    /** Record a DTN custody refusal by [uhid]. */
    open fun recordCustodyRefusal(uhid: String) {
        applyDelta(uhid, DELTA_CUSTODY_REFUSAL)
    }

    /**
     * Record a confirmed successful delivery via [uhid] with the observed
     * round-trip time. Positive signal: lifts score slightly.
     *
     * [roundTripMs] is accepted for API parity with the C# interface; the
     * in-memory implementation does not currently use it for weighting.
     */
    open fun recordDeliverySuccess(uhid: String, @Suppress("UNUSED_PARAMETER") roundTripMs: Int) {
        applyDelta(uhid, DELTA_DELIVERY_OK)
    }

    /** Record a delivery failure (lost bundle / unacknowledged hop) via [uhid]. */
    fun recordDeliveryFailure(uhid: String) {
        applyDelta(uhid, DELTA_DELIVERY_FAIL)
    }

    /**
     * Applies a pre-weighted delta (from reputation gossip) to [uhid]'s score.
     *
     * [weightedDelta] is clamped to [−1, 1] before application — this mirrors
     * the receive-side guard in [aethermesh.reputation.ReputationGossipService].
     */
    fun applyWeightedDelta(uhid: String, weightedDelta: Double) {
        val clamped = weightedDelta.coerceIn(-1.0, 1.0)
        applyDelta(uhid, clamped)
    }

    /**
     * Returns the current reputation score for [uhid] in [0.0, 1.0].
     * Returns 1.0 for unknown peers (benefit of the doubt until signals arrive).
     */
    fun getReputationScore(uhid: String): Double =
        scores[uhid] ?: 1.0

    /**
     * Returns a snapshot copy of all known reputation scores.
     * The map is safe to read after return; mutations to the service do not
     * affect it and mutations to the returned map do not affect the service.
     */
    fun getAllScores(): Map<String, Double> = HashMap(scores)

    // ── private helpers ──────────────────────────────────────────────────────

    /**
     * Snaps floating-point values within 1e-12 of 0 or 1 to avoid drift
     * accumulation (e.g. 5.5e-17 instead of exactly 0.0).
     */
    private fun clampScore(v: Double): Double {
        val clamped = v.coerceIn(0.0, 1.0)
        if (clamped < 1e-12) return 0.0
        if (clamped > 1.0 - 1e-12) return 1.0
        return clamped
    }

    /**
     * Atomically applies [delta] to [uhid]'s score.
     * New peers start at 1.0 before the delta is applied — matching
     * [AddOrUpdate] semantics in the C# reference (addValue = ClampScore(1.0 + delta)).
     */
    private fun applyDelta(uhid: String, delta: Double) {
        scores.compute(uhid) { _, current ->
            clampScore((current ?: 1.0) + delta)
        }
    }
}
