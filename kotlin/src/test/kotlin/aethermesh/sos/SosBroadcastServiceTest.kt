// SPDX-License-Identifier: MIT
package aethermesh.sos

import aethermesh.AetherMeshConstants
import aethermesh.FakeMeshSender
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFails
import kotlin.test.assertFalse
import kotlin.test.assertTrue

private const val LOCAL = "local"

private data class SosSvc(val svc: SosBroadcastService, val sender: FakeMeshSender)

private fun newSvc(): SosSvc {
    val sender = FakeMeshSender(LOCAL)
    return SosSvc(SosBroadcastService(sender), sender)
}

private fun newSosPacket(source: String, ttl: Int): MeshPacket {
    val body = "{\"broadcast_id\":\"${UUID.randomUUID()}\"," +
        "\"broadcast_type\":\"sos\",\"message\":\"help\"," +
        "\"latitude\":-33.9,\"longitude\":18.4,\"geohash\":null}"
    return MeshPacket(
        type = PacketType.SosBroadcast,
        sourceUhid = source,
        destinationUhid = "",
        ttl = ttl,
        priority = AetherMeshConstants.SOS_PRIORITY.toByte(),
        payload = body.toByteArray(Charsets.UTF_8),
    )
}

class SosBroadcastServiceTest {

    // ─── Broadcast ──────────────────────────────────────────

    @Test fun broadcast_floodsAndStoresAlert() = runBlocking {
        val (svc, sender) = newSvc()
        val ok = svc.broadcast("sos", "help", -33.9, 18.4, null)
        assertTrue(ok)
        assertEquals(1, sender.broadcasts.size)
        assertEquals(PacketType.SosBroadcast, sender.broadcasts[0].type)
        assertEquals(AetherMeshConstants.SOS_TTL, sender.broadcasts[0].ttl)
        assertEquals(AetherMeshConstants.SOS_PRIORITY.toByte(), sender.broadcasts[0].priority)
        assertEquals(1, svc.getActiveAlerts().size)
    }

    @Test fun broadcast_rateLimitedAfterMax() = runBlocking {
        val (svc, _) = newSvc()
        repeat(AetherMeshConstants.MAX_SOS_BROADCASTS_PER_HOUR) {
            assertTrue(svc.broadcast("sos", "h", 0.0, 0.0, null))
        }
        assertFalse(svc.broadcast("sos", "h", 0.0, 0.0, null))
    }

    @Test fun broadcast_rejectsEmptyType(): Unit = runBlocking {
        val (svc, _) = newSvc()
        assertFails {
            runBlocking { svc.broadcast("", "help", 0.0, 0.0, null) }
        }
    }

    // ─── Handle ─────────────────────────────────────────────

    @Test fun handle_dropsDuplicatePacketId() = runBlocking {
        val (svc, sender) = newSvc()
        val pkt = newSosPacket("alice", AetherMeshConstants.SOS_TTL)
        val pktId = pkt.id

        svc.handle(pkt)
        sender.clear()
        val alertsAfter = svc.getActiveAlerts().size

        val pkt2 = newSosPacket("alice", AetherMeshConstants.SOS_TTL).apply { id = pktId }
        svc.handle(pkt2)

        assertEquals(0, sender.broadcasts.size)
        assertEquals(alertsAfter, svc.getActiveAlerts().size)
    }

    @Test fun handle_ignoresSelfOriginated() = runBlocking {
        val (svc, sender) = newSvc()
        svc.handle(newSosPacket(LOCAL, AetherMeshConstants.SOS_TTL))
        assertEquals(0, sender.broadcasts.size)
    }

    @Test fun handle_rebroadcastsWhenTtlAllows() = runBlocking {
        val (svc, sender) = newSvc()
        svc.handle(newSosPacket("alice", 5))
        assertEquals(1, sender.broadcasts.size)
        assertEquals(4, sender.broadcasts[0].ttl)
    }

    @Test fun handle_doesNotRebroadcastWhenTtlExhausted() = runBlocking {
        val (svc, sender) = newSvc()
        svc.handle(newSosPacket("alice", 1))
        assertEquals(0, sender.broadcasts.size)
    }

    // ─── Resolve ────────────────────────────────────────────

    @Test fun resolve_removesAlert() = runBlocking {
        val (svc, _) = newSvc()
        svc.broadcast("sos", "h", 0.0, 0.0, null)
        val alerts = svc.getActiveAlerts()
        assertEquals(1, alerts.size)
        svc.resolve(alerts[0].id)
        assertTrue(svc.getActiveAlerts().isEmpty())
    }
}
