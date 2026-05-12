// SPDX-License-Identifier: MIT

package aether.security

data class AnomalyDetectorOptions(
    val volumeWindowMs: Long = 30_000L,
    val volumeSpikeMultiplier: Double = 5.0,
    val ewmaAlpha: Double = 0.20,
    val scatterWindowMs: Long = 60_000L,
    val scatterThreshold: Int = 50,
    val geohashPrefixLength: Int = 4,
    val geohashRateLimitMs: Long = 60_000L
)
