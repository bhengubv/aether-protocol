// SPDX-License-Identifier: MIT
package aethernet.sos

import aethernet.AetherNetConstants
import aethernet.FakeMeshSender
import aethernet.models.SosAcknowledgement
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFails
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for the SOS acknowledgement path ([PacketType.SosAck]). A receiving node sends a
 * directed ack back to the originator; the originator tallies distinct reach and emits
 * [SosBroadcastService.onSosAcknowledged]. Uses the in-memory [FakeMeshSender] — no transport
 * needed. Mirrors the C# SosAckTests.
 */
class SosAckTest {

    private fun build(sender: FakeMeshSender): SosBroadcastService = SosBroadcastService(sender)

    /** Originate a real SosBroadcast packet on a separate node and return it + its id. */
    private fun originateSos(originUhid: String): Pair<MeshPacket, UUID> = runBlocking {
        val originSender = FakeMeshSender(originUhid)
        val origin = build(originSender)
        origin.broadcast("medical", "help", -26.20, 28.04, "ke7g")
        originSender.broadcasts[0] to origin.getActiveAlerts()[0].id
    }

    private fun makeAck(broadcastId: UUID, responderUhid: String): MeshPacket = MeshPacket(
        type = PacketType.SosAck,
        sourceUhid = responderUhid,
        destinationUhid = "aether:origin:aa",
        payload = SosAckPayload(broadcastId = broadcastId, receivedAtMs = 1_700_000_000_000L).toJsonBytes(),
    )

    // ─── Directed-ack emission on receipt ───────────────────

    @Test fun handle_receivingSos_sendsDirectedAckToOriginator() = runBlocking {
        val (sos, id) = originateSos("aether:origin:aa")

        val receiverSender = FakeMeshSender("aether:receiver:bb")
        build(receiverSender).handle(sos)

        assertEquals(1, receiverSender.unicasts.size)
        val ack = receiverSender.unicasts[0]
        assertEquals(PacketType.SosAck, ack.packet.type)
        assertEquals("aether:origin:aa", ack.nextHopUhid)
        assertEquals("aether:origin:aa", ack.packet.destinationUhid)

        // The ack must carry the originator's broadcast id in its payload.
        val body = ack.packet.payload.toString(Charsets.UTF_8)
        assertTrue(body.contains("\"broadcast_id\":\"$id\""), "ack payload must reference the SOS id: $body")
    }

    @Test fun handle_ownSos_doesNotAck() = runBlocking {
        val localSender = FakeMeshSender("aether:origin:aa")
        val svc = build(localSender)
        svc.broadcast("panic", null, 0.0, 0.0, null)

        // Re-handling our own broadcast must not generate an ack.
        svc.handle(localSender.broadcasts[0])
        assertEquals(0, localSender.unicasts.size)
    }

    // ─── Ack handling on the originator ─────────────────────

    @Test fun handleAck_onOriginator_recordsResponderAndRaisesEvent() = runBlocking {
        val origin = build(FakeMeshSender("aether:origin:aa"))
        origin.broadcast("fire", "north wing", -26.1, 28.0, null)
        val id = origin.getActiveAlerts()[0].id

        var captured: SosAcknowledgement? = null
        origin.onSosAcknowledged = { captured = it }

        origin.handleAck(makeAck(id, "aether:responder:cc"))

        assertNotNull(captured)
        assertEquals(id, captured!!.broadcastId)
        assertEquals("aether:responder:cc", captured!!.responderUhid)
        assertEquals(1, captured!!.totalAcknowledgements)
        assertTrue(origin.getActiveAlerts()[0].acknowledgedBy.contains("aether:responder:cc"))
    }

    @Test fun handleAck_duplicateResponder_countedOnce() = runBlocking {
        val origin = build(FakeMeshSender("aether:origin:aa"))
        origin.broadcast("medical", null, 0.0, 0.0, null)
        val id = origin.getActiveAlerts()[0].id

        var events = 0
        origin.onSosAcknowledged = { events++ }

        origin.handleAck(makeAck(id, "aether:responder:cc"))
        origin.handleAck(makeAck(id, "aether:responder:cc")) // same responder again

        assertEquals(1, events)
        assertEquals(1, origin.getActiveAlerts()[0].acknowledgedBy.size)
    }

    @Test fun handleAck_twoDistinctResponders_countsTwo() = runBlocking {
        val origin = build(FakeMeshSender("aether:origin:aa"))
        origin.broadcast("medical", null, 0.0, 0.0, null)
        val id = origin.getActiveAlerts()[0].id

        origin.handleAck(makeAck(id, "aether:responder:cc"))
        origin.handleAck(makeAck(id, "aether:responder:dd"))

        assertEquals(2, origin.getActiveAlerts()[0].acknowledgedBy.size)
    }

    @Test fun handleAck_unknownBroadcast_isNoOp() = runBlocking {
        val svc = build(FakeMeshSender("aether:local:01"))
        var raised = false
        svc.onSosAcknowledged = { raised = true }

        svc.handleAck(makeAck(UUID.randomUUID(), "aether:responder:cc"))
        assertFalse(raised)
    }

    @Test fun handleAck_wrongPacketType_throws() {
        val svc = build(FakeMeshSender("aether:local:01"))
        val pkt = makeAck(UUID.randomUUID(), "aether:responder:cc").apply { type = PacketType.Data }
        assertFails { svc.handleAck(pkt) }
    }

    // ─── Byte-identity gate (fixtures/sos/vectors.json) ─────
    // snake_case, field order broadcast_id then received_at_ms, no whitespace, UUID lowercase-dashed,
    // received_at_ms a bare integer. Must be byte-identical with C# in every language port.

    @Test fun sosAckPayload_serializesToCanonicalBytes_vector1() {
        val json = SosAckPayload(
            broadcastId = UUID.fromString("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
            receivedAtMs = 1_700_000_000_000L,
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"broadcast_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"received_at_ms\":1700000000000}",
            json,
        )
    }

    @Test fun sosAckPayload_serializesToCanonicalBytes_vector2() {
        val json = SosAckPayload(
            broadcastId = UUID.fromString("00000000-0000-0000-0000-000000000000"),
            receivedAtMs = 0L,
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"broadcast_id\":\"00000000-0000-0000-0000-000000000000\",\"received_at_ms\":0}",
            json,
        )
    }

    // Guard: AetherNetConstants.SOS_* used by the directed ack are the same values the broadcast uses.
    @Test fun directedAck_usesSosTtlAndPriority() = runBlocking {
        val (sos, _) = originateSos("aether:origin:aa")
        val receiverSender = FakeMeshSender("aether:receiver:bb")
        build(receiverSender).handle(sos)
        val ack = receiverSender.unicasts[0].packet
        assertEquals(AetherNetConstants.SOS_TTL, ack.ttl)
        assertEquals(AetherNetConstants.SOS_PRIORITY.toByte(), ack.priority)
    }
}
