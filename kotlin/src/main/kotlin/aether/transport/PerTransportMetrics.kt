// SPDX-License-Identifier: MIT

package aether.transport

import java.util.concurrent.atomic.AtomicLong
import kotlin.concurrent.withLock
import kotlin.math.max

/**
 * Real-time per-transport EWMA metrics for adaptive transport selection.
 *
 * α = 0.2: the most-recent sample contributes 20 %; older history decays
 * by a factor of 0.8 per observation.
 *
 * Initial priors:
 *  - ewmaRttMs      = 200 ms   (conservative assumption for unknown links)
 *  - ewmaLossRate   = 0.05     (5 % initial loss assumption)
 *  - ewmaThroughput = 0 bps    (bootstrapped on first successful send)
 *
 * Thread-safe via [java.util.concurrent.locks.ReentrantLock].
 */
class PerTransportMetrics {

    companion object {
        private const val ALPHA = 0.2
    }

    private val lock = java.util.concurrent.locks.ReentrantLock()
    private val _sampleCount = AtomicLong(0)

    private var _ewmaRttMs: Double = 200.0
    private var _ewmaLossRate: Double = 0.05
    private var _ewmaThroughputBps: Double = 0.0

    // ── Read-only accessors ───────────────────────────────────────────────────

    val sampleCount: Long get() = _sampleCount.get()

    val ewmaRttMs: Double
        get() = lock.withLock { _ewmaRttMs }

    val ewmaLossRate: Double
        get() = lock.withLock { _ewmaLossRate }

    val ewmaThroughputBps: Double
        get() = lock.withLock { _ewmaThroughputBps }

    // ── Mutation ──────────────────────────────────────────────────────────────

    /**
     * Update EWMA state from one send observation.
     *
     * @param rttMs            Measured round-trip time in ms (0 = one-way send).
     * @param success          Whether the peer acknowledged receipt.
     * @param bytesTransferred Payload bytes on wire; used for throughput estimate.
     */
    fun recordSample(rttMs: Long, success: Boolean, bytesTransferred: Long) {
        lock.withLock {
            _sampleCount.incrementAndGet()

            if (rttMs > 0) {
                _ewmaRttMs = ALPHA * rttMs + (1 - ALPHA) * _ewmaRttMs
            }

            val lossObs = if (success) 0.0 else 1.0
            _ewmaLossRate = ALPHA * lossObs + (1 - ALPHA) * _ewmaLossRate

            if (success && rttMs > 0L && bytesTransferred > 0L) {
                val tputBps = bytesTransferred * 8.0 * 1000.0 / rttMs
                _ewmaThroughputBps = if (_ewmaThroughputBps < 1.0) {
                    tputBps // bootstrap first sample
                } else {
                    ALPHA * tputBps + (1 - ALPHA) * _ewmaThroughputBps
                }
            }
        }
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    /**
     * Composite score for transport ranking (higher = better).
     *
     * ```
     * score = (effectiveBps / powerCost) × (1 − lossRate) / max(rttMs, 1)
     * ```
     *
     * `effectiveBps` = max(ewmaThroughputBps, maxBandwidthBps × 0.1)
     * so that zero-sample transports still rank by their declared capacity.
     */
    fun compositeScore(maxBandwidthBps: Long, powerCostRelative: Int): Double {
        val power = max(powerCostRelative, 1)
        val (rtt, loss, tput) = lock.withLock {
            Triple(
                max(_ewmaRttMs, 1.0),
                _ewmaLossRate,
                _ewmaThroughputBps
            )
        }
        val effectiveBps = maxOf(tput, maxBandwidthBps * 0.1)
        return (effectiveBps / power) * (1.0 - loss) / rtt
    }
}
