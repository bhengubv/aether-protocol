// SPDX-License-Identifier: MIT

package aethernet.transport

/**
 * A transport paired with its pre-computed composite score.
 *
 * @property transport The underlying transport backend.
 * @property score     Composite score (higher = better to use right now).
 */
data class RankedTransport(
    val transport: TransportService,
    val score: Double,
)

/**
 * Orders available transports by composite score (highest first).
 *
 * Unavailable transports are excluded from the result.
 * For transports without live [PerTransportMetrics], a static prior
 * derived from `maxBandwidthBps / powerCostRelative` is used so that
 * bootstrapping still produces a sensible ordering.
 *
 * @param transports All registered transport backends.
 * @return Sorted list of [RankedTransport]; highest score first.
 */
fun rankTransports(transports: Iterable<TransportService>): List<RankedTransport> {
    return transports
        .filter { it.isAvailable }
        .map { t ->
            val score = t.metrics?.compositeScore(t.maxBandwidthBps, t.powerCostRelative)
                ?: run {
                    val power = maxOf(t.powerCostRelative, 1)
                    t.maxBandwidthBps.toDouble() / power
                }
            RankedTransport(transport = t, score = score)
        }
        .sortedByDescending { it.score }
}
