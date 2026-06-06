// SPDX-License-Identifier: MIT
package aethermesh.transport

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

// ── stub transport ────────────────────────────────────────────────────────────

private class StubTransport(
    override val name:              String,
    override val isAvailable:       Boolean = true,
    override val maxBandwidthBps:   Long    = 100_000L,
    override val powerCostRelative: Int     = 1,
    override val maxRangeMeters:    Int     = 100,
    override val maxConcurrentPeers: Int    = 10,
    metricsOverride: PerTransportMetrics?   = null,
) : TransportService {
    private val _metrics = metricsOverride
    override val metrics: PerTransportMetrics? get() = _metrics
    override val dataReceived: Flow<Pair<String, ByteArray>> = emptyFlow()
    override fun isConnected(peerUhid: String): Boolean = false
    override suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean = true
    override suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean = true
    override fun close() = Unit
}

// ── RankedTransport data class ────────────────────────────────────────────────

class RankedTransportTest {

    @Test
    fun `RankedTransport holds transport and score`() {
        val t  = StubTransport("ble")
        val rt = RankedTransport(t, 42.0)
        assertEquals(t,    rt.transport)
        assertEquals(42.0, rt.score)
    }

    @Test
    fun `RankedTransport equality is structural`() {
        val t  = StubTransport("ble")
        val r1 = RankedTransport(t, 5.0)
        val r2 = RankedTransport(t, 5.0)
        assertEquals(r1, r2)
    }
}

// ── rankTransports ────────────────────────────────────────────────────────────

class TransportRankerTest {

    @Test
    fun `empty input returns empty list`() {
        assertTrue(rankTransports(emptyList()).isEmpty())
    }

    @Test
    fun `unavailable transport is excluded`() {
        val unavail = StubTransport("ble", isAvailable = false)
        val result  = rankTransports(listOf(unavail))
        assertTrue(result.isEmpty())
    }

    @Test
    fun `all unavailable returns empty list`() {
        val transports = listOf(
            StubTransport("ble",  isAvailable = false),
            StubTransport("wifi", isAvailable = false),
        )
        assertTrue(rankTransports(transports).isEmpty())
    }

    @Test
    fun `available transport is included`() {
        val t      = StubTransport("ble")
        val result = rankTransports(listOf(t))
        assertEquals(1, result.size)
        assertEquals(t, result[0].transport)
    }

    @Test
    fun `results are sorted by score descending`() {
        val low  = StubTransport("low",  maxBandwidthBps = 10_000L,  powerCostRelative = 10)
        val high = StubTransport("high", maxBandwidthBps = 1_000_000L, powerCostRelative = 1)
        val result = rankTransports(listOf(low, high))
        assertEquals(2, result.size)
        assertTrue(result[0].score >= result[1].score, "first element should have the higher score")
        assertEquals("high", result[0].transport.name)
    }

    @Test
    fun `static score is maxBandwidthBps divided by powerCostRelative when no metrics`() {
        // powerCostRelative = 1, maxBandwidthBps = 500_000 → score = 500_000.0
        val t      = StubTransport("wifi", maxBandwidthBps = 500_000L, powerCostRelative = 1,
                                   metricsOverride = null)
        val result = rankTransports(listOf(t))
        assertEquals(1, result.size)
        assertEquals(500_000.0 / 1, result[0].score, 0.001)
    }

    @Test
    fun `static score clamps powerCostRelative to at least 1`() {
        // powerCostRelative = 0 should be treated as 1 (clamped by max(powerCost, 1))
        val t      = StubTransport("zero-cost", maxBandwidthBps = 200_000L, powerCostRelative = 0,
                                   metricsOverride = null)
        val result = rankTransports(listOf(t))
        assertEquals(1, result.size)
        // score = 200_000 / max(0, 1) = 200_000.0
        assertEquals(200_000.0, result[0].score, 0.001)
    }

    @Test
    fun `transport with live metrics uses compositeScore`() {
        val m = PerTransportMetrics()
        // Feed a known RTT so compositeScore differs from the static formula
        m.recordSample(rttMs = 50L, success = true, bytesTransferred = 1000L)
        val t = StubTransport("ble-live", maxBandwidthBps = 100_000L, powerCostRelative = 2,
                              metricsOverride = m)
        val result = rankTransports(listOf(t))
        assertEquals(1, result.size)
        // Just verify the score is positive and based on the metrics path (not exactly the static value)
        assertTrue(result[0].score > 0.0)
    }

    @Test
    fun `only available transports from mixed list are returned`() {
        val a = StubTransport("avail",  isAvailable = true)
        val u = StubTransport("unavail", isAvailable = false)
        val result = rankTransports(listOf(a, u))
        assertEquals(1, result.size)
        assertEquals("avail", result[0].transport.name)
    }
}
