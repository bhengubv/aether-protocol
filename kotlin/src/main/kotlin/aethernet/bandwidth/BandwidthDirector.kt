// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/**
 * Cross-transport bandwidth synthesis and mesh gossip coordinator.
 *
 * Maintains a matrix of (peerUhid × transportName) → [BandwidthSample] estimates
 * and provides transport recommendations based on payload size, BDP, and power cost.
 *
 * ## Transport selection algorithm
 * 1. Score = AvailableBps / PowerCostRelative (higher is better).
 * 2. If payload > BDP: prefer the transport with the largest BDP (reduces round-trips).
 * 3. Penalise transports with [BandwidthConfidence.NONE] by 50 % (untrusted estimate).
 *
 * Thread-safe via [ConcurrentHashMap].
 *
 * Gossip warm-start: unique to AetherNet. QUIC and TCP always cold-start at ~14.6 kB/s
 * (RFC 6928 §2); gossip warming lets a new session begin with a non-zero estimate.
 */
class BandwidthDirector {

    // (peerUhid, transportName) → latest sample
    private val matrix = ConcurrentHashMap<Pair<String, String>, BandwidthSample>()

    // transportName → estimator
    private val estimators = ConcurrentHashMap<String, BandwidthEstimator>()

    companion object {
        /** Power costs per transport name (lower = preferred). */
        private val DEFAULT_POWER_COSTS: Map<String, Int> = mapOf(
            "NearLink"     to 1,
            "BLE"          to 2,
            "Wi-Fi Direct" to 3,
            "CircleLink"   to 3,
            "QUIC Relay"   to 10,
            "HTTP Relay"   to 10,
        )
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /**
     * Register a transport estimator with this director.
     * Called once per transport at node startup.
     */
    fun register(estimator: BandwidthEstimator) {
        estimators[estimator.transportName] = estimator
        estimator.onSampleImproved.add { sample ->
            // When any estimator fires, update every known peer's entry for this transport.
            matrix.keys
                .filter { it.second.equals(sample.transportName, ignoreCase = true) }
                .forEach { key -> matrix[key] = sample }
        }
    }

    // ── Estimates ─────────────────────────────────────────────────────────────

    /**
     * Get the bandwidth estimate for a specific peer on a specific transport.
     * Returns null if no estimate exists yet.
     */
    fun getEstimate(peerUhid: String, transport: String): BandwidthSample? =
        matrix[peerUhid to transport]

    /**
     * Get all current estimates for a peer across all transports,
     * ranked by [BandwidthSample.availableBps] descending.
     */
    fun getEstimates(peerUhid: String): List<BandwidthSample> =
        matrix.entries
            .filter { it.key.first.equals(peerUhid, ignoreCase = true) }
            .map { it.value }
            .sortedByDescending { it.availableBps }

    // ── Transport recommendation ───────────────────────────────────────────────

    /**
     * Recommend the best transport for a payload of [payloadBytes].
     * Returns null if no transports are registered.
     */
    fun recommendTransport(peerUhid: String, payloadBytes: Long): String? {
        val candidates = getEstimates(peerUhid)
        if (candidates.isEmpty()) {
            // No measurement data — fall back to the registered transport with lowest power cost.
            return estimators.values
                .sortedBy { DEFAULT_POWER_COSTS[it.transportName] ?: 5 }
                .firstOrNull()?.transportName
        }

        var best: BandwidthSample? = null
        // NEGATIVE_INFINITY (not Double.MIN_VALUE, which is the smallest *positive*
        // double) so a zero-scoring first candidate is still selected — matches
        // C#'s double.MinValue semantics.
        var bestScore = Double.NEGATIVE_INFINITY

        for (s in candidates) {
            val powerCost = (DEFAULT_POWER_COSTS[s.transportName] ?: 5).toDouble()
            val available = s.availableBps.toDouble()
            // Oversize payloads get a NEUTRAL 1.0 (not 0.0) so the available-bandwidth/
            // power term still ranks them — keeps selection identical across all 8 SDKs.
            val bdpBonus = if (payloadBytes > s.bdpBytes) 1.0 else 1.5
            val confidenceFactor = if (s.confidence == BandwidthConfidence.NONE) 0.5 else 1.0
            val score = (available / powerCost) * bdpBonus * confidenceFactor
            if (score > bestScore) {
                bestScore = score
                best = s
            }
        }

        return best?.transportName
    }

    // ── Gossip ────────────────────────────────────────────────────────────────

    /**
     * Build a gossip payload for a new peer that has just completed handshake.
     * Returns null if the estimator for [transportName] has no reliable estimate.
     */
    fun buildGossipPayload(peerUhid: String, transportName: String): BandwidthGossipPayload? {
        val estimator = estimators[transportName] ?: return null
        val s = estimator.currentSample
        if (s.confidence == BandwidthConfidence.NONE) return null

        return BandwidthGossipPayload(
            peerUhid = peerUhid,
            transportName = transportName,
            btlBwBps = s.btlBwBps,
            rtPropUs = s.rtProp.toNanos() / 1_000L,
            confidence = s.confidence,
            measuredAt = s.measuredAt,
        )
    }

    /**
     * Receive and apply a gossip payload from a remote peer.
     * Warms the local estimator for the named transport and seeds the matrix
     * so [getEstimate] returns a value even before the first active probe.
     */
    fun applyGossip(payload: BandwidthGossipPayload) {
        val estimator = estimators[payload.transportName] ?: return
        estimator.warmFromGossip(
            payload.btlBwBps,
            Duration.ofNanos(payload.rtPropUs * 1_000L),
            payload.confidence,
        )
        // Seed the matrix so getEstimate returns something immediately.
        matrix[payload.peerUhid to payload.transportName] = estimator.currentSample
    }
}
