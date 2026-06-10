// SPDX-License-Identifier: MIT

package aethernet.bandwidth

import java.time.Duration
import java.time.Instant
import java.util.ArrayDeque
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/**
 * Reference BBRv3-inspired bandwidth estimator for a single transport link.
 *
 * ## Algorithm summary
 * - **BtlBw (bottleneck bandwidth):** rolling maximum of per-delivery delivery-rate
 *   samples over a [BtlBwWindowSize] = 10-RTprop window. Uses the maximum (not average)
 *   to track pipe capacity rather than current load — mirrors BBRv3 BtlBwFilter
 *   (draft-cardwell-iccrg-bbr-congestion-control-02 §4.3.2.1).
 * - **RTprop (path propagation delay):** rolling minimum RTT over a
 *   [RT_PROP_WINDOW_MS] = 10 000 ms window. The minimum filters out queueing delay.
 * - **SRTT / RTTVAR:** RFC 6298 §2.3 Jacobson/Karels algorithm. α = 1/8, β = 1/4.
 * - **Loss rate:** EWMA with α = [LOSS_ALPHA] = 0.10.
 * - **PHY cap:** RSSI-to-BtlBw mapping from IEEE 802.11 / Bluetooth SIG Core Spec 5.4
 *   constrains estimates on weak radio links before probe data arrives.
 *
 * Thread safety: all mutable state is protected by a single [ReentrantLock].
 * [currentSample] is a @Volatile reference — readers never block on the lock.
 *
 * @param transportName Transport identifier (e.g. "BLE", "NearLink", "Wi-Fi Direct").
 * @param maxBandwidthBps Theoretical maximum bandwidth for this transport (bps).
 *   Used as an optimistic initial estimate; PHY hints and probes tighten it quickly.
 */
class BandwidthEstimator(val transportName: String, val maxBandwidthBps: Long) {

    // ── Constants ─────────────────────────────────────────────────────────────

    companion object {
        /** Number of delivery-rate samples in the BtlBw max-filter window. */
        const val BtlBwWindowSize = 10

        /** Minimum RTT window duration in milliseconds (BBRv3 ProbeRTT period). */
        const val RT_PROP_WINDOW_MS = 10_000.0

        /** EWMA loss rate smoothing factor (α). */
        const val LOSS_ALPHA = 0.10

        /** RFC 6298 SRTT smoothing factor (1/8). */
        private const val SRTT_ALPHA = 0.125

        /** RFC 6298 RTTVAR smoothing factor (1/4). */
        private const val RTT_VAR_BETA = 0.25

        /** Minimum BtlBw improvement fraction to fire [onSampleImproved]. */
        private const val IMPROVEMENT_THRESHOLD = 0.05
    }

    // ── Lock ──────────────────────────────────────────────────────────────────

    private val lock = ReentrantLock()

    // ── BtlBw max-filter: circular buffer ────────────────────────────────────

    private val btlBwWindow = Array(BtlBwWindowSize) { 0L to 0.0 } // (rateBps, timestampMs)
    private var btlBwHead = 0
    private var btlBwCount = 0

    // ── RTprop min-filter ─────────────────────────────────────────────────────

    private val rtPropSamples = ArrayDeque<Pair<Double, Double>>() // (rttMs, timestampMs)

    // ── RFC 6298 SRTT / RTTVAR ────────────────────────────────────────────────

    private var srttMs = 0.0
    private var rttVarMs = 0.0
    private var firstRtt = true

    // ── Loss EWMA ─────────────────────────────────────────────────────────────

    private var lossRate = 0.0

    // ── PHY cap ───────────────────────────────────────────────────────────────

    private var phyCapBps = 0L

    // ── Confidence tracking ───────────────────────────────────────────────────

    private var probeRounds = 0
    private var warmedFromGossip = false

    // ── Snapshot cache ────────────────────────────────────────────────────────

    @Volatile
    private var _current: BandwidthSample = buildSnapshot(maxBandwidthBps, Duration.ofMillis(50))

    /** Full snapshot of the current estimate (immutable — safe to share across threads). */
    val currentSample: BandwidthSample get() = _current

    // ── Callbacks (Kotlin equivalent of C# event) ─────────────────────────────

    /**
     * Listeners notified when BtlBw improves by ≥ 5 % or [BandwidthConfidence] advances.
     * Consumers: ABR controller, transport selector, streaming bitrate ladder.
     * Callbacks are invoked on a daemon thread outside the lock to avoid deadlocks.
     */
    val onSampleImproved: MutableList<(BandwidthSample) -> Unit> = mutableListOf()

    // ── Observation feed ──────────────────────────────────────────────────────

    /**
     * Record a successful delivery of [bytes].
     * Both timestamps are microseconds since Unix epoch on the **same clock**.
     */
    fun recordDelivery(bytes: Int, sendUs: Long, deliverUs: Long) {
        if (bytes <= 0 || deliverUs <= sendUs) return

        val elapsedMs = (deliverUs - sendUs) / 1000.0
        val deliveryRateBps = (bytes * 8.0 / (elapsedMs / 1000.0)).toLong()
        val rttMs = elapsedMs // one-way treated conservatively as RTT

        lock.withLock {
            addToBtlBwWindow(deliveryRateBps, nowMs())
            updateRttEstimates(rttMs)
            probeRounds++
            commit()
        }
    }

    /** Record that [bytes] were lost (timeout or explicit NAK). */
    fun recordLoss(bytes: Int) {
        if (bytes <= 0) return
        lock.withLock {
            lossRate = LOSS_ALPHA * 1.0 + (1 - LOSS_ALPHA) * lossRate
            commit()
        }
    }

    /**
     * Feed an active probe ack into the estimator.
     * [localReceiveUs] is the local clock µs at ACK receipt.
     */
    fun recordProbeResult(ack: BandwidthProbeAck, localReceiveUs: Long) {
        val rtt = ack.rtt
        if (rtt <= Duration.ZERO || rtt > Duration.ofSeconds(30)) return

        val deliveryRateBps = if (ack.probeBytes > 0)
            (ack.probeBytes * 8.0 / rtt.toMillis() * 1000.0).toLong()
        else 0L

        lock.withLock {
            updateRttEstimates(rtt.toMillis().toDouble())
            if (deliveryRateBps > 0) addToBtlBwWindow(deliveryRateBps, nowMs())
            probeRounds++
            commit()
        }
    }

    /**
     * Pre-warm from a gossip payload. Only effective when [BandwidthConfidence] is
     * [BandwidthConfidence.NONE] — never downgrades an existing estimate.
     */
    fun warmFromGossip(btlBwBps: Long, rtProp: Duration, confidence: BandwidthConfidence) {
        lock.withLock {
            if (probeRounds > 0 || warmedFromGossip) return@withLock // never downgrade
            addToBtlBwWindow(btlBwBps, nowMs())
            val rttMs = rtProp.toMillis().toDouble()
            if (rttMs > 0) {
                srttMs = rttMs
                rttVarMs = rttMs / 2.0
                firstRtt = false
                addToRtPropWindow(rttMs, nowMs())
            }
            warmedFromGossip = true
            commit()
        }
    }

    /**
     * Apply a physical-layer hint. RSSI-to-BtlBw caps the estimate before probes complete.
     *
     * BLE calibration from Bluetooth SIG Core Spec 5.4 Table 7.2 (2Msym/s PHY):
     * - ≥ −70 dBm → up to   2 000 kbps
     * - ≥ −85 dBm → up to     500 kbps
     * - ≥ −95 dBm → up to     125 kbps
     * - < −95 dBm → up to      40 kbps (marginal link)
     *
     * Wi-Fi (802.11ax) calibration from 3GPP TS 36.213 Annex A:
     * - ≥ −50 dBm → up to 600 Mbps
     * - ≥ −67 dBm → up to 200 Mbps
     * - ≥ −80 dBm → up to  54 Mbps
     * - < −80 dBm → up to  11 Mbps
     *
     * BLE table used as conservative fallback for all transports.
     *
     * @param rssiDbm Received signal strength in dBm.
     */
    fun applyPhyHint(rssiDbm: Int) {
        val cap = when {
            rssiDbm >= -50 -> 600_000_000L
            rssiDbm >= -67 -> 200_000_000L
            rssiDbm >= -70 ->   2_000_000L
            rssiDbm >= -80 ->  54_000_000L
            rssiDbm >= -85 ->     500_000L
            rssiDbm >= -95 ->     125_000L
            else           ->      40_000L
        }
        lock.withLock {
            phyCapBps = cap
            commit()
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /**
     * RFC 6298 §2.3 RTT sample integration.
     * First sample: SRTT = R, RTTVAR = R/2.
     * Subsequent: RTTVAR = (1−β)×RTTVAR + β×|SRTT−R|; SRTT = (1−α)×SRTT + α×R.
     */
    private fun updateRttEstimates(rttMs: Double) {
        if (firstRtt) {
            srttMs = rttMs
            rttVarMs = rttMs / 2.0
            firstRtt = false
        } else {
            rttVarMs = (1 - RTT_VAR_BETA) * rttVarMs + RTT_VAR_BETA * Math.abs(srttMs - rttMs)
            srttMs = (1 - SRTT_ALPHA) * srttMs + SRTT_ALPHA * rttMs
        }
        // Success sample → decay loss EWMA toward zero.
        lossRate = LOSS_ALPHA * 0.0 + (1 - LOSS_ALPHA) * lossRate
        addToRtPropWindow(rttMs, nowMs())
    }

    /**
     * Insert a delivery-rate sample into the max-filter window.
     * Discards old samples whose timestamp is outside 10×RTprop.
     */
    private fun addToBtlBwWindow(rateBps: Long, nowMs: Double) {
        val windowDurationMs = 10.0 * maxOf(1.0, minRtPropMs())
        val expiry = nowMs - windowDurationMs

        // Evict expired tail entries.
        while (btlBwCount > 0) {
            val tail = (btlBwHead + BtlBwWindowSize - btlBwCount) % BtlBwWindowSize
            if (btlBwWindow[tail].second < expiry) btlBwCount-- else break
        }

        btlBwWindow[btlBwHead] = rateBps to nowMs
        btlBwHead = (btlBwHead + 1) % BtlBwWindowSize
        if (btlBwCount < BtlBwWindowSize) btlBwCount++
    }

    private fun addToRtPropWindow(rttMs: Double, nowMs: Double) {
        rtPropSamples.addLast(rttMs to nowMs)
        while (rtPropSamples.isNotEmpty() &&
            rtPropSamples.peekFirst()!!.second < nowMs - RT_PROP_WINDOW_MS) {
            rtPropSamples.pollFirst()
        }
    }

    private fun maxBtlBwBps(): Long {
        if (btlBwCount == 0) return 0L
        var max = 0L
        for (i in 0 until btlBwCount) {
            val idx = (btlBwHead + BtlBwWindowSize - btlBwCount + i) % BtlBwWindowSize
            if (btlBwWindow[idx].first > max) max = btlBwWindow[idx].first
        }
        return max
    }

    private fun minRtPropMs(): Double {
        if (rtPropSamples.isEmpty()) return if (srttMs > 0) srttMs else 50.0
        var min = Double.MAX_VALUE
        for ((rttMs, _) in rtPropSamples) if (rttMs < min) min = rttMs
        return if (min > 0) min else 1.0
    }

    private fun computeConfidence(): BandwidthConfidence = when {
        probeRounds == 0 && !warmedFromGossip -> BandwidthConfidence.NONE
        probeRounds == 0                      -> BandwidthConfidence.LOW
        probeRounds < 5                       -> BandwidthConfidence.LOW
        probeRounds < 20                      -> BandwidthConfidence.MEDIUM
        else                                  -> BandwidthConfidence.HIGH
    }

    /** Rebuild the snapshot and fire [onSampleImproved] if significant. */
    private fun commit() {
        val prev = _current
        _current = buildSnapshot(maxBtlBwBps(), Duration.ofMillis(minRtPropMs().toLong().coerceAtLeast(1L)))
        val cur = _current

        if (prev.btlBwBps == 0L ||
            (cur.btlBwBps - prev.btlBwBps) > prev.btlBwBps * IMPROVEMENT_THRESHOLD ||
            cur.confidence > prev.confidence) {
            // Fire outside the lock to avoid deadlocks. Plain daemon thread for JVM 11 compat.
            val listeners = onSampleImproved.toList()
            if (listeners.isNotEmpty()) {
                val t = Thread { listeners.forEach { it(cur) } }
                t.isDaemon = true
                t.start()
            }
        }
    }

    private fun buildSnapshot(btlBw: Long, rtProp: Duration): BandwidthSample {
        // Preserve fractional-millisecond precision (matches C#'s TimeSpan.FromMilliseconds(double)).
        // Floor srtt at 1.0 ms and rttVar at 0.0 ms BEFORE converting, mirroring
        // Math.Max(1.0, _srttMs) / Math.Max(0.0, _rttVarMs) in the C# reference.
        val srtt = Duration.ofNanos((maxOf(1.0, srttMs) * 1_000_000.0).toLong())
        val rttVar = Duration.ofNanos((maxOf(0.0, rttVarMs) * 1_000_000.0).toLong())
        val clampedLoss = lossRate.coerceIn(0.0, 1.0)
        // BDP and available are derived from the EFFECTIVE (PHY-capped) rate, not the raw BtlBw.
        val effective = if (phyCapBps > 0) minOf(btlBw, phyCapBps) else btlBw
        val available = (effective * (1.0 - clampedLoss)).toLong()
        val bdp = if (effective > 0) (effective / 8.0 * (rtProp.toMillis() / 1000.0)).toLong() else 0L

        return BandwidthSample(
            transportName = transportName,
            btlBwBps = effective,
            availableBps = available,
            bdpBytes = bdp,
            srtt = srtt,
            rttVar = rttVar,
            rtProp = rtProp,
            lossRate = lossRate,
            phyCapBps = phyCapBps,
            confidence = computeConfidence(),
            measuredAt = Instant.now(),
        )
    }

    private fun nowMs(): Double =
        System.currentTimeMillis().toDouble()
}
