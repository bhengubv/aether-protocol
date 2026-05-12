// SPDX-License-Identifier: MIT
// Unit tests for PredictiveTransportSelector — Kalman RTT filter and scoring.

package aether.transport

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import org.junit.jupiter.api.Test
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

// ── FakeTransport — minimal TransportService stub ─────────────────────────────

private class FakeTransport(
    override val name:              String,
    override val maxBandwidthBps:   Long    = 500_000L,
    override val powerCostRelative: Int     = 1,
    override val isAvailable:       Boolean = true,
) : TransportService {
    override val maxRangeMeters:    Int  = 100
    override val maxConcurrentPeers: Int = 10
    override val metrics: PerTransportMetrics = PerTransportMetrics()
    override val dataReceived: Flow<Pair<String, ByteArray>> = emptyFlow()

    override fun isConnected(peerUhid: String): Boolean = false
    override suspend fun sendAsync(peerUhid: String, data: ByteArray): Boolean = true
    override suspend fun sendStreamAsync(peerUhid: String, data: ByteArray): Boolean = true
    override fun close() = Unit
}

// ── Kalman filter (indirect) ──────────────────────────────────────────────────

class KalmanFilterTest {

    @Test
    fun kalmanConvergesOnSteadyState() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        sel.register(t, 200.0)

        repeat(50) { sel.observeMetrics(t, 100L, success = true, bytesTransferred = 1000L) }

        val state = sel.getKalmanState(t)
        assertNotNull(state)
        val (rtt, _, _) = state
        assertTrue(abs(rtt - 100.0) < 5.0,
            "Kalman did not converge: rtt=${"%.2f".format(rtt)}, want ~100")
    }

    @Test
    fun posteriorVarianceDecreasesWithObservations() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        sel.register(t, 200.0)

        val initialVariance = sel.getKalmanState(t)!!.third

        repeat(10) { sel.observeMetrics(t, 200L, success = true, bytesTransferred = 1000L) }

        val afterVariance = sel.getKalmanState(t)!!.third
        assertTrue(afterVariance < initialVariance,
            "posterior variance $afterVariance should be < initial $initialVariance")
    }

    @Test
    fun driftIsPositiveForRisingRtt() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        sel.register(t, 100.0)

        for (i in 0 until 10) {
            sel.observeMetrics(t, (100 + (i + 1) * 15).toLong(), success = true, bytesTransferred = 1000L)
        }

        val state = sel.getKalmanState(t)
        assertNotNull(state)
        val (_, drift, _) = state
        assertTrue(drift > 0.0,
            "drift ${"%.4f".format(drift)} should be positive for rising RTT")
    }
}

// ── PredictiveTransportSelector lifecycle ─────────────────────────────────────

class PredictiveSelectorTest {

    @Test
    fun registerAndRankFastTransportFirst() {
        val sel  = PredictiveTransportSelector()
        val fast = FakeTransport("fast", maxBandwidthBps = 1_000_000L, powerCostRelative = 1)
        val slow = FakeTransport("slow", maxBandwidthBps = 10_000L,    powerCostRelative = 10)
        sel.register(fast, 50.0)
        sel.register(slow, 150.0)

        repeat(5) { sel.observeMetrics(fast, 50L, success = true, bytesTransferred = 1000L) }

        val ranked = sel.rank(100)
        assertEquals(2, ranked.size)
        assertEquals("fast", ranked[0].transport.name,
            "expected 'fast' first, got '${ranked[0].transport.name}'")
    }

    @Test
    fun unavailableTransportExcludedFromRank() {
        val sel     = PredictiveTransportSelector()
        val avail   = FakeTransport("avail",   isAvailable = true)
        val unavail = FakeTransport("unavail", isAvailable = false)
        sel.register(avail,   100.0)
        sel.register(unavail, 100.0)

        val ranked = sel.rank()
        assertEquals(1, ranked.size)
        assertEquals("avail", ranked[0].transport.name)
    }

    @Test
    fun unregisterRemovesTransport() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        sel.register(t, 100.0)
        sel.unregister(t)
        assertEquals(0, sel.rank().size)
    }

    @Test
    fun selectBestReturnsNullWhenEmpty() {
        val sel = PredictiveTransportSelector()
        assertNull(sel.selectBest())
    }

    @Test
    fun duplicateRegisterIsNoOp() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        sel.register(t, 100.0)
        sel.register(t, 200.0)
        assertEquals(1, sel.rank().size)
    }

    @Test
    fun getKalmanStateInitialValues() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        sel.register(t, 123.0)

        val state = sel.getKalmanState(t)
        assertNotNull(state)
        val (rtt, drift, variance) = state
        assertTrue(abs(rtt - 123.0) < 1e-9,  "initial rtt $rtt != 123.0")
        assertTrue(abs(drift) < 1e-9,         "initial drift $drift != 0.0")
        assertTrue(variance > 0.0,            "initial variance $variance should be > 0")
    }

    @Test
    fun getKalmanStateUnregisteredReturnsNull() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        assertNull(sel.getKalmanState(t))
    }

    @Test
    fun rankReturnsPositiveScore() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        sel.register(t, 100.0)

        val ranked = sel.rank()
        assertEquals(1, ranked.size)
        assertTrue(ranked[0].score > 0.0)
    }

    @Test
    fun scoreImprovesAfterGoodObservations() {
        val sel = PredictiveTransportSelector()
        val t   = FakeTransport("t")
        sel.register(t, 200.0)
        val scoreBefore = sel.rank()[0].score

        repeat(10) { sel.observeMetrics(t, 20L, success = true, bytesTransferred = 5000L) }

        val scoreAfter = sel.rank()[0].score
        assertTrue(scoreAfter > scoreBefore,
            "score should improve after good observations (before=$scoreBefore, after=$scoreAfter)")
    }
}
