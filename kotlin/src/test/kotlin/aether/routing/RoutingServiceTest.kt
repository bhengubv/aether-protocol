// SPDX-License-Identifier: MIT
package aether.routing

import aether.AetherConstants
import aether.FakeMeshSender
import aether.models.RouteEntry
import aether.protocol.MeshPacket
import aether.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

private const val LOCAL = "local-uhid"

private data class Svc(
    val svc: RoutingService,
    val sender: FakeMeshSender,
    val store: InMemoryRouteStore,
)

private fun newSvc(verifier: RouteReplyVerifier = AcceptAllRouteReplyVerifier()): Svc {
    val sender = FakeMeshSender(LOCAL)
    val store = InMemoryRouteStore()
    return Svc(RoutingService(sender, store, verifier), sender, store)
}

private fun newRreq(source: String, dest: String, ttl: Int = AetherConstants.DEFAULT_TTL) =
    MeshPacket(type = PacketType.RouteRequest, sourceUhid = source, destinationUhid = dest, ttl = ttl)

private fun newRrep(source: String, dest: String, ttl: Int = AetherConstants.DEFAULT_TTL) =
    MeshPacket(type = PacketType.RouteReply, sourceUhid = source, destinationUhid = dest, ttl = ttl)

class RoutingServiceTest {

    // ─── HandleRouteRequest ──────────────────────────────────

    @Test fun handleRreq_dropsDuplicateById() = runBlocking {
        val (svc, sender, _) = newSvc()
        val rreq = newRreq("alice", "bob")
        svc.handleRouteRequest(rreq)
        sender.clear()
        svc.handleRouteRequest(rreq)
        assertEquals(0, sender.broadcasts.size)
        assertEquals(0, sender.unicasts.size)
    }

    @Test fun handleRreq_ignoresSelfOriginated() = runBlocking {
        val (svc, sender, store) = newSvc()
        svc.handleRouteRequest(newRreq(LOCAL, "bob"))
        assertEquals(0, sender.broadcasts.size)
        assertEquals(0, sender.unicasts.size)
        assertEquals(0, store.getAll().size)
    }

    @Test fun handleRreq_installsReverseRouteToSource() = runBlocking {
        val (svc, _, store) = newSvc()
        svc.handleRouteRequest(newRreq("alice", "bob"))
        val r = store.get("alice")
        assertNotNull(r)
        assertEquals("alice", r!!.nextHopUhid)
        assertTrue(r.hopCount >= 1)
    }

    @Test fun handleRreq_asDestination_sendsRrepBack() = runBlocking {
        val (svc, sender, _) = newSvc()
        svc.handleRouteRequest(newRreq("alice", LOCAL))
        assertEquals(1, sender.unicasts.size)
        val rec = sender.unicasts[0]
        assertEquals(PacketType.RouteReply, rec.packet.type)
        assertEquals(LOCAL, rec.packet.sourceUhid)
        assertEquals("alice", rec.packet.destinationUhid)
        assertEquals("alice", rec.nextHopUhid)
    }

    @Test fun handleRreq_withCachedRouteToDestination_repliesOnBehalf() = runBlocking {
        val (svc, sender, store) = newSvc()
        store.save(RouteEntry(
            destinationUhid = "carol",
            nextHopUhid = "carol",
            hopCount = 1,
            qualityScore = 50,
            expiresAt = Instant.now().plusSeconds(300),
        ))
        svc.findRoute("carol")
        sender.clear()

        svc.handleRouteRequest(newRreq("alice", "carol"))

        val rrep = sender.unicasts.firstOrNull { it.packet.type == PacketType.RouteReply }?.packet
            ?: sender.broadcasts.firstOrNull { it.type == PacketType.RouteReply }
        assertNotNull(rrep, "expected an RREP")
        assertEquals("carol", rrep!!.sourceUhid)
    }

    @Test fun handleRreq_forwardsWhenTtlAllows() = runBlocking {
        val (svc, sender, _) = newSvc()
        svc.handleRouteRequest(newRreq("alice", "carol", ttl = 5))
        assertEquals(1, sender.broadcasts.size)
        assertEquals(4, sender.broadcasts[0].ttl)
    }

    @Test fun handleRreq_dropsWhenTtlExhausted() = runBlocking {
        val (svc, sender, _) = newSvc()
        svc.handleRouteRequest(newRreq("alice", "carol", ttl = 1))
        assertEquals(0, sender.broadcasts.size)
        assertEquals(0, sender.unicasts.size)
    }

    // ─── HandleRouteReply ────────────────────────────────────

    @Test fun handleRrep_installsForwardRoute() = runBlocking {
        val (svc, _, store) = newSvc()
        svc.handleRouteReply(newRrep("carol", LOCAL))
        val r = store.get("carol")
        assertNotNull(r)
        assertEquals("carol", r!!.nextHopUhid)
    }

    @Test fun handleRrep_rejectsWhenVerifierFails() = runBlocking {
        val rejecting = object : RouteReplyVerifier {
            override suspend fun verify(routeReply: MeshPacket): Boolean = false
        }
        val (svc, _, store) = newSvc(rejecting)
        svc.handleRouteReply(newRrep("carol", LOCAL))
        assertNull(store.get("carol"))
    }

    @Test fun handleRrep_forwardsTowardOriginalRequester() = runBlocking {
        val (svc, sender, store) = newSvc()
        store.save(RouteEntry(
            destinationUhid = "alice",
            nextHopUhid = "bob",
            hopCount = 2,
            qualityScore = 50,
            expiresAt = Instant.now().plusSeconds(300),
        ))
        svc.findRoute("alice")
        sender.clear()

        svc.handleRouteReply(newRrep("carol", "alice", ttl = 4))

        val fwd = sender.unicasts.firstOrNull {
            it.packet.type == PacketType.RouteReply && it.nextHopUhid == "bob"
        }
        assertNotNull(fwd)
        assertEquals(3, fwd!!.packet.ttl)
    }

    // ─── FindRoute / Prune ────────────────────────────────────

    @Test fun findRoute_returnsCachedWithoutBroadcasting() = runBlocking {
        val (svc, sender, store) = newSvc()
        store.save(RouteEntry(
            destinationUhid = "bob",
            nextHopUhid = "bob",
            hopCount = 1,
            qualityScore = 50,
            expiresAt = Instant.now().plusSeconds(300),
        ))
        val r = svc.findRoute("bob")
        assertNotNull(r)
        assertEquals("bob", r!!.nextHopUhid)
        assertEquals(0, sender.broadcasts.size)
    }

    @Test fun findRoute_returnsNullWhenNoPeers() = runBlocking {
        val (svc, _, _) = newSvc()
        assertNull(svc.findRoute("bob"))
    }

    @Test fun pruneAsync_removesExpiredRoutes(): Unit = runBlocking {
        val (svc, _, store) = newSvc()
        store.save(RouteEntry(
            destinationUhid = "stale",
            nextHopUhid = "stale",
            hopCount = 1,
            qualityScore = 50,
            expiresAt = Instant.now().minusSeconds(10),
        ))
        store.save(RouteEntry(
            destinationUhid = "fresh",
            nextHopUhid = "fresh",
            hopCount = 1,
            qualityScore = 50,
            expiresAt = Instant.now().plusSeconds(300),
        ))
        svc.findRoute("fresh")
        svc.prune()
        assertNull(store.get("stale"))
        assertNotNull(store.get("fresh"))
    }
}
