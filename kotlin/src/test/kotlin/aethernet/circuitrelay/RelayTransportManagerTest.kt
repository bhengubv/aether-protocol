// SPDX-License-Identifier: MIT

package aethernet.circuitrelay

import aethernet.protocol.MeshPacket
import aethernet.transport.TransportManager
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancel
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Test
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Gap-2 acceptance test (Kotlin mirror of the C# `CircuitRelayMeshIntegrationTests
 * .Relay_Is_Auto_Selected_By_TransportManager_As_Fallback`): the circuit-relay engine must be picked
 * **automatically** by [TransportManager] as the last-resort fallback — NOT called directly.
 *
 * A and B each run a manager whose ONLY transport is the relay [CircuitRelayTransport] (cost 90).
 * `aMgr.sendAsync` routes B's payload through the manager's power-cost selection (the additional-
 * transports fall-through, cost 90), B receives it, and — crucially — it surfaces through
 * [TransportManager.dataReceived] tagged with the relay transport's name `"Circuit Relay (v2)"`,
 * proving selection rather than hand-wiring. R shows exactly one active bridge.
 *
 * No kotlinx.coroutines in the engine: the in-process hub delivers each MeshPacket on a cached
 * thread pool (like a real async transport, and to avoid re-entrant deadlock while the engine
 * blocks on its CONNECT/RESERVE waits).
 */
class RelayTransportManagerTest {

    /** In-process mesh, adjacency A-R-B with NO direct A-B edge; routes each MeshPacket one hop. */
    private class MeshHub(private val pool: ExecutorService) {
        private val lock = ReentrantLock()
        private val edges = HashSet<String>()
        private val links = HashMap<String, MeshRelayLink>()

        fun connect(x: String, y: String) = lock.withLock {
            edges.add("$x|$y"); edges.add("$y|$x")
        }
        private fun adjacent(x: String, y: String): Boolean = lock.withLock { edges.contains("$x|$y") }
        fun register(node: String, link: MeshRelayLink) = lock.withLock { links[node] = link }

        fun sendFrom(node: String): (MeshPacket) -> Boolean = { pkt ->
            if (!adjacent(node, pkt.destinationUhid)) {
                false
            } else {
                val l = lock.withLock { links[pkt.destinationUhid] }
                if (l != null) pool.execute { l.handleIncomingPacket(pkt) } // async one-hop delivery
                true
            }
        }
        fun canReachFrom(node: String): (String) -> Boolean = { other -> adjacent(node, other) }
    }

    @Test
    fun `relay is auto-selected by TransportManager as fallback`() = runBlocking {
        val pool = Executors.newCachedThreadPool()
        // Independent scope for each manager's inbound collectors (relay sends block on IO).
        val aMgrScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val bMgrScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        try {
            val hub = MeshHub(pool)
            hub.connect("A", "R")
            hub.connect("R", "B") // deliberately NO A-B edge

            val (aT, aL) = MeshCircuitRelay.create("A", hub.sendFrom("A"), hub.canReachFrom("A"))
            val (rT, rL) = MeshCircuitRelay.create("R", hub.sendFrom("R"), hub.canReachFrom("R"))
            val (bT, bL) = MeshCircuitRelay.create("B", hub.sendFrom("B"), hub.canReachFrom("B"))
            hub.register("A", aL)
            hub.register("R", rL)
            hub.register("B", bL)

            // A and B each run a TransportManager whose ONLY transport is the relay.
            val aMgr = TransportManager(listOf(aT), aMgrScope)
            val bMgr = TransportManager(listOf(bT), bMgrScope)

            // B surfaces the relayed message through TransportManager.dataReceived (replay = 0, so we
            // must be collecting BEFORE A sends). Tuple: (sender, payload, viaTransportName).
            val received = CompletableDeferred<Triple<String, ByteArray, String>>()
            val sub = bMgrScope.launch { bMgr.dataReceived.collect { received.complete(it) } }
            yield() // let bMgr's per-transport collector AND this subscriber start

            assertTrue(bT.reserveAsync("R"), "B.reserve(R) failed")   // B reserves on the relay
            aT.setRoute("B", "R")                                     // A learns B is reachable via R

            val payload = byteArrayOf(0x11, 0x22, 0x33, 0x44)
            assertTrue(aMgr.sendAsync("B", payload), "aMgr.sendAsync must select the relay and succeed")

            val got = withTimeoutOrNull(3000) { received.await() }
            sub.cancel()
            assertNotNull(got, "B never received the relayed message via TransportManager selection")
            assertEquals("A", got.first, "sender")
            assertContentEquals(payload, got.second, "payload")
            assertEquals("Circuit Relay (v2)", got.third, "the manager must have chosen the relay, by name")
            assertEquals(1, rT.activeBridgeCount, "R is genuinely bridging over real packets")

            aMgr.close()
            bMgr.close()
        } finally {
            aMgrScope.cancel()
            bMgrScope.cancel()
            pool.shutdownNow()
            pool.awaitTermination(2, TimeUnit.SECONDS)
        }
    }
}
