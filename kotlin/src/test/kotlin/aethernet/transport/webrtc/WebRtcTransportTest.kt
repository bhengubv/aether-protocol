// SPDX-License-Identifier: MIT

package aethernet.transport.webrtc

import dev.onvoid.webrtc.PeerConnectionFactory
import dev.onvoid.webrtc.RTCIceServer
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Assumptions.assumeTrue
import org.junit.jupiter.api.BeforeEach
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

    // Skip (don't fail) when the webrtc-java native library can't load — e.g. a headless CI
    // runner with no native libwebrtc. Mirrors the Swift/C AETHERNET_WITH_WEBRTC gating and
    // Python's importorskip("aiortc"): the loopback proof needs the real native stack.
    @BeforeEach
    fun requireWebRtcNative() {
        assumeTrue(webRtcNativeAvailable, "webrtc-java native library not available in this environment")
    }

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

    companion object {
        // dev.onvoid.webrtc loads native libwebrtc on first PeerConnectionFactory construction;
        // on a runner without that native library the class init throws (ExceptionInInitializerError).
        // Probe once so requireWebRtcNative() can skip rather than crash the whole class.
        private val webRtcNativeAvailable: Boolean = runCatching {
            PeerConnectionFactory().dispose()
            true
        }.getOrDefault(false)
    }
}
