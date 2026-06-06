// SPDX-License-Identifier: MIT
// Predictive transport selector — 2-state Kalman RTT filter over PerTransportMetrics.
//
// Why Kalman over EWMA?
// ─────────────────────
// EWMA is a 1-pole IIR: it smooths past measurements but cannot predict future RTT
// when a link is actively degrading.  The Kalman filter models RTT as a constant-
// velocity process [rtt, drift]:
//
//   x_t = F * x_{t−1} + w   (F = [[1,1],[0,1]])
//   z_t = H * x_t   + v    (H = [1,0])
//
// Positive drift signals a rising RTT *before* it exceeds a threshold, enabling
// proactive transport switching.  The posterior variance further penalises uncertain
// links even when their point estimate looks good.
//
// Score formula:
//   (effectiveBps / powerCost) × (1 − lossRate) / max(kalmanRtt, 1) × (1 / (1 + σ/100))

package aethernet.transport

import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.concurrent.read
import kotlin.concurrent.write
import kotlin.math.max
import kotlin.math.sqrt

// ── KalmanRttFilter ───────────────────────────────────────────────────────────

/**
 * Two-state Kalman filter estimating RTT and drift for one transport link.
 *
 * State: x = [rtt; drift] — F = [[1,1],[0,1]], H = [1,0].
 *
 * **Not thread-safe.** Callers must hold the selector's write lock before
 * calling [update].
 */
private class KalmanRttFilter(
    initialRttMs:        Double = 200.0,
    private val qRtt:    Double = 25.0,
    private val qDrift:  Double = 5.0,
    private val r:       Double = 100.0,
) {
    // ── State (backing fields are writeable only within this class) ───────
    var rtt:   Double = initialRttMs; private set
    var drift: Double = 0.0;          private set
    var p00:   Double = 400.0;        private set
    var p01:   Double = 0.0;          private set
    var p11:   Double = 100.0;        private set

    /** Posterior variance of the RTT estimate (ms²). Lower = more confident. */
    val rttVariance: Double get() = p00

    /**
     * Incorporate a new RTT measurement and return the updated estimate.
     *
     * Full Kalman predict→update cycle:
     *  1. Predict:  x̂ = F·x,  P̂ = F·P·Fᵀ + Q
     *  2. Gain:     S = H·P̂·Hᵀ + R,  K = P̂·Hᵀ / S
     *  3. Update:   x = x̂ + K·(z − H·x̂),  P = (I − K·H)·P̂
     */
    fun update(measuredRttMs: Double): Double {
        // ── 1. Predict ────────────────────────────────────────────────────────
        val rttPred   = rtt + drift
        val driftPred = drift

        // P_pred = F·P·Fᵀ + Q  (F = [[1,1],[0,1]])
        val pp00 = p00 + 2.0 * p01 + p11 + qRtt
        val pp01 = p01 + p11
        val pp11 = p11 + qDrift

        // ── 2. Kalman gain (H = [1, 0]) ──────────────────────────────────────
        val s  = pp00 + r
        val k0 = pp00 / s
        val k1 = pp01 / s

        // ── 3. Update ─────────────────────────────────────────────────────────
        val innovation = measuredRttMs - rttPred
        rtt   = rttPred   + k0 * innovation
        drift = driftPred + k1 * innovation

        // P = (I − K·H)·P_pred
        p00 = (1.0 - k0) * pp00
        p01 = (1.0 - k0) * pp01
        p11 = -k1 * pp01 + pp11

        // Clamp to prevent numerical drift below zero.
        p00 = maxOf(p00, 1e-6)
        p11 = maxOf(p11, 1e-6)

        return rtt
    }
}

// ── PredictiveTransportSelector ───────────────────────────────────────────────

/**
 * A transport paired with its Kalman-predictive score and uncertainty metadata.
 *
 * @property transport      The ranked transport backend.
 * @property score          Composite predictive score (higher = better).
 * @property predictedRttMs Kalman-estimated RTT in milliseconds.
 * @property rttVariance    Posterior RTT variance (ms²); lower = more confident.
 */
data class RankedTransportPredictive(
    val transport:      TransportService,
    val score:          Double,
    val predictedRttMs: Double,
    val rttVariance:    Double,
)

/**
 * Predictive transport selector maintaining a Kalman RTT filter per transport.
 *
 * Extends [rankTransports] by replacing the EWMA RTT term with a Kalman-estimated
 * RTT (which can predict rising RTT before it crosses a threshold) and adding a
 * reliability penalty proportional to the Kalman variance.
 *
 * Thread-safe via [ReentrantReadWriteLock].
 *
 * ### Usage
 * ```kotlin
 * val selector = PredictiveTransportSelector()
 * transports.forEach { selector.register(it) }
 *
 * // After each send:
 * selector.observeMetrics(transport, rttMs = 42L, success = true, bytesTransferred = 1024L)
 *
 * // Get the best transport for a 500-byte payload:
 * val best = selector.selectBest(payloadBytes = 500)
 * ```
 */
class PredictiveTransportSelector {

    private val lock    = ReentrantReadWriteLock()
    private val filters = mutableMapOf<TransportService, KalmanRttFilter>()

    // ── Registration ──────────────────────────────────────────────────────

    /**
     * Register [transport] for Kalman tracking with an optional initial RTT prior.
     * Safe to call multiple times — subsequent calls for already-registered
     * transports are no-ops.
     */
    fun register(transport: TransportService, initialRttMs: Double = 200.0): Unit =
        lock.write {
            filters.getOrPut(transport) { KalmanRttFilter(initialRttMs) }
        }

    /** Remove [transport] and discard its Kalman state. */
    fun unregister(transport: TransportService): Unit = lock.write {
        filters.remove(transport)
    }

    // ── Observation ───────────────────────────────────────────────────────

    /**
     * Feed a new sample to both the transport's [PerTransportMetrics] EWMA
     * and our Kalman filter.  Call after every completed send attempt.
     *
     * Only successful sends with `rttMs > 0` update the Kalman state —
     * failures carry no useful propagation-delay signal.
     */
    fun observeMetrics(
        transport:        TransportService,
        rttMs:            Long,
        success:          Boolean,
        bytesTransferred: Long,
    ) {
        transport.metrics?.recordSample(rttMs, success, bytesTransferred)

        if (rttMs <= 0L || !success) return

        lock.write {
            filters[transport]?.update(rttMs.toDouble())
        }
    }

    // ── Ranking ───────────────────────────────────────────────────────────

    /**
     * Return transports sorted by predictive score (highest first).
     *
     * Only available transports are included.  [payloadBytes] is used to
     * exclude transports whose max bandwidth would require > 30 s to
     * serialise this payload.
     */
    fun rank(payloadBytes: Int = 512): List<RankedTransportPredictive> = lock.read {
        val result = mutableListOf<RankedTransportPredictive>()

        for ((transport, filter) in filters) {
            if (!transport.isAvailable) continue

            val bw = transport.maxBandwidthBps
            if (bw > 0L) {
                val serialSec = payloadBytes * 8.0 / bw.toDouble()
                if (serialSec > 30.0) continue
            }

            val kalmanRtt = maxOf(filter.rtt, 1.0)
            val variance  = filter.rttVariance
            val stddev    = sqrt(variance)
            val power     = maxOf(transport.powerCostRelative, 1).toDouble()

            val lossRate: Double
            val effectiveBps: Double

            val m = transport.metrics
            if (m != null) {
                lossRate     = m.ewmaLossRate
                effectiveBps = maxOf(m.ewmaThroughputBps, bw * 0.1)
            } else {
                lossRate     = 0.05
                effectiveBps = bw * 0.1
            }

            // Reliability factor: 1.0 at σ=0 ms, ~0.5 at σ=100 ms.
            val reliabilityFactor = 1.0 / (1.0 + stddev / 100.0)
            val score = (effectiveBps / power) * (1.0 - lossRate) / kalmanRtt * reliabilityFactor

            result += RankedTransportPredictive(
                transport      = transport,
                score          = score,
                predictedRttMs = kalmanRtt,
                rttVariance    = variance,
            )
        }

        result.sortedByDescending { it.score }
    }

    /**
     * Return the highest-scoring available transport for [payloadBytes], or `null`.
     */
    fun selectBest(payloadBytes: Int = 512): TransportService? =
        rank(payloadBytes).firstOrNull()?.transport

    /**
     * Return `Triple(rttMs, driftMs, variance)` for a registered transport,
     * or `null` if the transport is not registered.
     */
    fun getKalmanState(transport: TransportService): Triple<Double, Double, Double>? =
        lock.read {
            val f = filters[transport] ?: return@read null
            Triple(f.rtt, f.drift, f.p00)
        }
}
