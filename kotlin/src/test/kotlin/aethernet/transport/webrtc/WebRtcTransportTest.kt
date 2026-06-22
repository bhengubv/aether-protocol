// SPDX-License-Identifier: MIT

package aethernet.transport.webrtc

import dev.onvoid.webrtc.RTCIceServer
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Loopback proof for the real WebRTC data-channel transport.
 *
 * Stands up two real [WebRtcTransport] instances wired only through an in-process signalling bus —
 * no central server, no STUN — and proves a direct data channel negotiates over host candidates and
 * carries bytes. Mirrors the Go `TestTwoPeersExchangeBytesNoServer` and the C# loopback test.
 */
class WebRtcTransportTest {

    // Empty (not null) => host-candidate-only ICE: no STUN, no network dependency.
    private fun hostOnly(): List<RTCIceServer> = emptyList()

    @Test
    fun `two peers exchange bytes over a serverless data channel`() = runBlocking {
        val bus = InMemorySignalingBus()
        val alice = WebRtcTransport("alice", bus.endpoint("alice"), hostOnly())
        val bob = WebRtcTransport("bob", bus.endpoint("bob"), hostOnly())

        try {
            // SharedFlow(replay=0): subscribe BEFORE the send triggers negotiation, or the
            // delivered datum is lost.
            val received = CompletableDeferred<Pair<String, ByteArray>>()
            val collector = launch {
                bob.dataReceived.collect { received.complete(it) }
            }
            yield() // let the collector start

            val payload = "hello over a serverless webrtc datachannel".toByteArray()
            val ok = alice.sendAsync("bob", payload)
            assertTrue(ok, "alice.sendAsync should succeed once the channel opens")

            val result = withTimeoutOrNull(30_000) { received.await() }
            collector.cancel()

            assertNotNull(result, "timed out waiting for bytes over the data channel")
            assertEquals("alice", result.first)
            assertContentEquals(payload, result.second)

            assertTrue(alice.isConnected("bob"), "alice should report connected to bob")
            assertTrue(bob.isConnected("alice"), "bob should report connected to alice")
        } finally {
            alice.close()
            bob.close()
            bus.close()
        }
    }

    @Test
    fun `transport metadata is ladder-facing`() {
        val bus = InMemorySignalingBus()
        val transport = WebRtcTransport("x", bus.endpoint("x"), hostOnly())
        try {
            assertEquals("WebRTC P2P", transport.name)
            assertTrue(transport.isAvailable)
            assertEquals(0, transport.maxRangeMeters, "internet range should be 0 (unbounded)")
            assertTrue(transport.maxBandwidthBps > 0)
            assertNotNull(transport.metrics)
        } finally {
            transport.close()
            bus.close()
        }
    }
}
