// SPDX-License-Identifier: MIT
package aethernet.heartbeat

import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for [HeartbeatService] (PacketType.Heartbeat). Uses the in-memory [FakeMeshSender] —
 * no transport needed. Mirrors the C# HeartbeatTests.
 */
private const val LOCAL = "aether:local:01"

private data class HbSvc(val svc: HeartbeatService, val sender: FakeMeshSender)

private fun newSvc(localUhid: String = LOCAL): HbSvc {
    val sender = FakeMeshSender(localUhid)
    return HbSvc(HeartbeatService(sender), sender)
}

private fun heartbeatFrom(source: String, sequence: Int, sentAtMs: Long): MeshPacket = MeshPacket(
    type = PacketType.Heartbeat,
    sourceUhid = source,
    destinationUhid = "*",
    payload = HeartbeatPayload(sequence = sequence, sentAtMs = sentAtMs).toJsonBytes(),
)

class HeartbeatServiceTest {

    // ─── Byte-identity gate (fixtures/heartbeat/vectors.json) ─────
    // snake_case, field order sequence then sent_at_ms, no whitespace, both values bare integers.
    // Must be byte-identical with C# in every language port.

    @Test fun heartbeatPayload_serializesToCanonicalBytes_vector1() {
        val json = HeartbeatPayload(sequence = 1, sentAtMs = 1_700_000_000_000L)
            .toJsonBytes().toString(Charsets.UTF_8)
        assertEquals("{\"sequence\":1,\"sent_at_ms\":1700000000000}", json)
    }

    @Test fun heartbeatPayload_serializesToCanonicalBytes_vector2() {
        val json = HeartbeatPayload(sequence = 0, sentAtMs = 0L)
            .toJsonBytes().toString(Charsets.UTF_8)
        assertEquals("{\"sequence\":0,\"sent_at_ms\":0}", json)
    }

    // ─── Send ───────────────────────────────────────────────

    @Test fun send_broadcastsHeartbeat_withIncrementingSequence() = runBlocking {
        val (svc, sender) = newSvc()

        val d1 = svc.sendHeartbeat()
        val d2 = svc.sendHeartbeat()

        assertEquals(2, sender.broadcasts.size)
        sender.broadcasts.forEach { assertEquals(PacketType.Heartbeat, it.type) }
        sender.broadcasts.forEach { assertEquals(1, it.ttl) }
        sender.broadcasts.forEach { assertEquals("*", it.destinationUhid) }
        sender.broadcasts.forEach { assertEquals(LOCAL, it.sourceUhid) }

        val first = sender.broadcasts[0].payload.toString(Charsets.UTF_8)
        val second = sender.broadcasts[1].payload.toString(Charsets.UTF_8)
        assertTrue(first.contains("\"sequence\":1"), "first beat seq=1: $first")
        assertTrue(second.contains("\"sequence\":2"), "second beat seq=2: $second")

        // FakeMeshSender.broadcast returns the connected-peer count (0 here — no peers added).
        assertEquals(0, d1)
        assertEquals(0, d2)
    }

    @Test fun send_returnsDeliveredPeerCount() = runBlocking {
        val sender = FakeMeshSender(LOCAL)
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:aa", identityKey = ByteArray(0)))
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:bb", identityKey = ByteArray(0)))
        val svc = HeartbeatService(sender)

        assertEquals(2, svc.sendHeartbeat())
    }

    // ─── Handle ─────────────────────────────────────────────

    @Test fun handle_recordsPeerAndRaisesEvent() = runBlocking {
        val (svc, _) = newSvc()
        var seen: PeerLiveness? = null
        svc.onPeerSeen = { seen = it }

        val ok = svc.handle(heartbeatFrom("aether:peer:aa", 7, 1_700_000_000_000L))

        assertTrue(ok)
        assertNotNull(seen)
        assertEquals("aether:peer:aa", seen!!.uhid)
        assertEquals(7, seen!!.lastSequence)
        assertEquals(1_700_000_000_000L, seen!!.lastSentAtMs)

        val known = svc.getKnownPeers()
        assertEquals(1, known.size)
        assertEquals("aether:peer:aa", known[0].uhid)
    }

    @Test fun handle_refreshesExistingPeer() = runBlocking {
        val (svc, _) = newSvc()
        svc.handle(heartbeatFrom("aether:peer:aa", 1, 1000L))
        svc.handle(heartbeatFrom("aether:peer:aa", 2, 2000L))

        val known = svc.getKnownPeers()
        assertEquals(1, known.size)
        assertEquals(2, known[0].lastSequence)
    }

    @Test fun handle_ownHeartbeat_isIgnored() = runBlocking {
        val (svc, _) = newSvc(LOCAL)
        val ok = svc.handle(heartbeatFrom(LOCAL, 1, 1000L))
        assertFalse(ok)
        assertTrue(svc.getKnownPeers().isEmpty())
    }

    @Test fun handle_wrongPacketType_returnsFalse() = runBlocking {
        val (svc, _) = newSvc()
        val pkt = heartbeatFrom("aether:peer:aa", 1, 1000L).apply { type = PacketType.Data }
        assertFalse(svc.handle(pkt))
        assertTrue(svc.getKnownPeers().isEmpty())
    }

    @Test fun handle_malformedPayload_returnsFalse() = runBlocking {
        val (svc, _) = newSvc()
        val pkt = MeshPacket(
            type = PacketType.Heartbeat,
            sourceUhid = "aether:peer:aa",
            destinationUhid = "*",
            payload = "not json".toByteArray(Charsets.UTF_8),
        )
        assertFalse(svc.handle(pkt))
        assertTrue(svc.getKnownPeers().isEmpty())
    }

    // ─── GetLivePeers ───────────────────────────────────────

    @Test fun getLivePeers_includesRecentlySeenPeer() = runBlocking {
        val (svc, _) = newSvc()
        svc.handle(heartbeatFrom("aether:peer:aa", 1, 1000L))

        // A just-received heartbeat is live within any generous window.
        val live = svc.getLivePeers(withinSeconds = 3600)
        assertEquals(1, live.size)
        assertEquals("aether:peer:aa", live[0].uhid)

        // A negative window pushes the recency horizon into the future, so it excludes even a
        // just-seen peer — a deterministic proof the filter filters (no wall-clock race).
        assertTrue(svc.getLivePeers(withinSeconds = -1).isEmpty())
    }
}
