// SPDX-License-Identifier: MIT

package aethermesh.security

import java.util.concurrent.ConcurrentHashMap

/**
 * Detects behavioural anomalies from network peers and feeds signals into
 * [NodeReputationService].
 *
 * Three detection axes:
 *  1. **Volume spike** — per-source packet rate vs. EWMA baseline.
 *  2. **Destination scatter** — unique destinations within a rolling window.
 *  3. **Geohash mismatch** — claimed location vs. observed routing location.
 *
 * All per-UHID state is thread-safe via fine-grained per-key lock objects
 * stored in [ConcurrentHashMap]s.
 */
class BehavioralAnomalyDetector(
    private val reputation: NodeReputationService,
    private val opts: AnomalyDetectorOptions = AnomalyDetectorOptions()
) {

    // ── Volume tracking ──────────────────────────────────────────────────────

    private data class VolumeState(
        var windowStart: Long = 0L,
        var windowCount: Int = 0,
        var ewmaBaseline: Double = 0.0,
        var hasBaseline: Boolean = false
    )

    /** Lock objects — one per source UHID. */
    private val volumeLocks = ConcurrentHashMap<String, Any>()

    /** Mutable volume state — one entry per source UHID. */
    private val volumeData = ConcurrentHashMap<String, VolumeState>()

    // ── Scatter tracking ─────────────────────────────────────────────────────

    /** Lock objects — one per source UHID. */
    private val scatterLocks = ConcurrentHashMap<String, Any>()

    /**
     * List of (destinationUhid, timestampMs) pairs seen within
     * [AnomalyDetectorOptions.scatterWindowMs].
     */
    private val scatterData = ConcurrentHashMap<String, MutableList<Pair<String, Long>>>()

    // ── Geohash rate limiting ────────────────────────────────────────────────

    /** Lock objects for atomic check-then-act in geohash mismatch handling. */
    private val geoLocks = ConcurrentHashMap<String, Any>()

    /** Timestamp (ms) of the last geohash-mismatch signal fired for each UHID. */
    private val geoLastSignal = ConcurrentHashMap<String, Long>()

    // ── Public API ───────────────────────────────────────────────────────────

    /**
     * Record that a packet was seen from [sourceUhid] bound for [destinationUhid]
     * at [timestampMs].  Runs volume-spike and scatter checks.
     */
    fun observePacket(sourceUhid: String, destinationUhid: String, timestampMs: Long) {
        checkVolume(sourceUhid, timestampMs)
        checkScatter(sourceUhid, destinationUhid, timestampMs)
    }

    /**
     * Record that [uhid] claimed [claimedGeohash] in its packet header but was
     * routed from a location whose geohash prefix is [observedRoutingGeohash].
     *
     * If the first [AnomalyDetectorOptions.geohashPrefixLength] characters
     * differ **and** the per-UHID rate limit has expired, fires
     * [NodeReputationService.recordSignatureFailure].
     */
    fun observeGeohashClaim(
        uhid: String,
        claimedGeohash: String,
        observedRoutingGeohash: String,
        timestampMs: Long = System.currentTimeMillis()
    ) {
        val claimedPrefix  = claimedGeohash.take(opts.geohashPrefixLength)
        val observedPrefix = observedRoutingGeohash.take(opts.geohashPrefixLength)

        if (claimedPrefix == observedPrefix) return

        // Rate-limit: at most one signal per geohashRateLimitMs per UHID.
        val lock = geoLocks.computeIfAbsent(uhid) { Any() }
        synchronized(lock) {
            val lastSignal = geoLastSignal[uhid]
            if (lastSignal == null || timestampMs - lastSignal >= opts.geohashRateLimitMs) {
                geoLastSignal[uhid] = timestampMs
                reputation.recordSignatureFailure(uhid)
            }
        }
    }

    /**
     * Direct passthrough: record that an SPK (Ed25519) signature verification
     * failed for a packet from [uhid].
     */
    fun observeSpkSigFailure(uhid: String) {
        reputation.recordSignatureFailure(uhid)
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private fun checkVolume(sourceUhid: String, timestampMs: Long) {
        val lock  = volumeLocks.computeIfAbsent(sourceUhid) { Any() }
        val state = volumeData.computeIfAbsent(sourceUhid) { VolumeState(windowStart = timestampMs) }

        synchronized(lock) {
            if (timestampMs - state.windowStart >= opts.volumeWindowMs) {
                // ── Roll the window ──────────────────────────────────────────
                if (!state.hasBaseline) {
                    // First roll: seed EWMA from this window's count
                    state.ewmaBaseline = state.windowCount.toDouble()
                    state.hasBaseline  = true
                } else {
                    // Update EWMA
                    state.ewmaBaseline =
                        opts.ewmaAlpha * state.windowCount + (1.0 - opts.ewmaAlpha) * state.ewmaBaseline

                    // Spike check — only when EWMA is positive to avoid ÷0
                    if (state.windowCount > opts.volumeSpikeMultiplier * state.ewmaBaseline &&
                        state.ewmaBaseline > 0.0) {
                        reputation.recordRreqFloodAttempt(sourceUhid)
                    }
                }

                // Start fresh window
                state.windowStart = timestampMs
                state.windowCount = 1
            } else {
                state.windowCount += 1
            }
        }
    }

    private fun checkScatter(sourceUhid: String, destinationUhid: String, timestampMs: Long) {
        val lock    = scatterLocks.computeIfAbsent(sourceUhid) { Any() }
        val entries = scatterData.computeIfAbsent(sourceUhid) { mutableListOf() }

        synchronized(lock) {
            entries.add(Pair(destinationUhid, timestampMs))

            // Prune entries outside the rolling window
            entries.removeAll { timestampMs - it.second > opts.scatterWindowMs }

            // Count unique destinations still inside the window
            val uniqueDests = entries.map { it.first }.toSet().size
            if (uniqueDests > opts.scatterThreshold) {
                reputation.recordRreqFloodAttempt(sourceUhid)
            }
        }
    }
}
