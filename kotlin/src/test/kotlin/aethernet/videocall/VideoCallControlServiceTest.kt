// SPDX-License-Identifier: MIT
package aethernet.videocall

import aethernet.AetherNetConstants
import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for [VideoCallControlService] (PacketType.VideoCall call-control). Uses the in-memory
 * [FakeMeshSender] — no transport needed. Directed signalling. Mirrors the C# VideoCallControlTests.
 */
private const val LOCAL = "aether:local:01"

private fun controlPacket(
    callId: UUID,
    action: String,
    fromUhid: String
): MeshPacket = MeshPacket(
    type = PacketType.VideoCall,
    sourceUhid = fromUhid,
    destinationUhid = LOCAL,
    payload = VideoCallControlPayload(callId = callId, action = action, sentAtMs = 1L).toJsonBytes(),
)

class VideoCallControlServiceTest {

    // ─── Byte-identity gate (fixtures/videocall/vectors.json) ─────
    // snake_case, field order call_id, action, sent_at_ms, no whitespace, lowercase-dashed UUID,
    // sent_at_ms a bare integer. Must be byte-identical with C# in every port.

    @Test fun videoCallControlPayload_serializesToCanonicalBytes_vector1() {
        val json = VideoCallControlPayload(
            callId = UUID.fromString("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
            action = "ring",
            sentAtMs = 1_700_000_000_000L
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"call_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"action\":\"ring\",\"sent_at_ms\":1700000000000}",
            json
        )
    }

    @Test fun videoCallControlPayload_serializesToCanonicalBytes_vector2() {
        val json = VideoCallControlPayload(
            callId = UUID.fromString("00000000-0000-0000-0000-000000000000"),
            action = "hangup",
            sentAtMs = 0L
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"call_id\":\"00000000-0000-0000-0000-000000000000\",\"action\":\"hangup\",\"sent_at_ms\":0}",
            json
        )
    }

    // ─── Ring ───────────────────────────────────────────────

    @Test fun ring_sendsDirectedRingToPeer_andReturnsCallId() = runBlocking {
        val sender = FakeMeshSender("aether:alice:01")
        val svc = VideoCallControlService(sender)

        val callId = svc.ring("aether:bob:02")

        assertNotEquals(UUID(0L, 0L), callId)
        assertEquals(1, sender.unicasts.size)
        val sent = sender.unicasts[0]
        assertEquals(PacketType.VideoCall, sent.packet.type)
        assertEquals("aether:bob:02", sent.nextHopUhid)
        assertEquals(AetherNetConstants.DEFAULT_TTL, sent.packet.ttl)
        val body = sent.packet.payload.toString(Charsets.UTF_8)
        assertTrue(body.contains("\"action\":\"ring\""), body)
        assertTrue(body.contains("\"call_id\":\"$callId\""), body)
    }

    // ─── Respond (accept / decline / hangup) ────────────────

    @Test fun accept_sendsDirectedAcceptToPeer() = runBlocking { assertRespond("accept") { svc, id -> svc.accept(id, "aether:bob:02") } }

    @Test fun decline_sendsDirectedDeclineToPeer() = runBlocking { assertRespond("decline") { svc, id -> svc.decline(id, "aether:bob:02") } }

    @Test fun hangup_sendsDirectedHangupToPeer() = runBlocking { assertRespond("hangup") { svc, id -> svc.hangup(id, "aether:bob:02") } }

    private suspend fun assertRespond(action: String, act: suspend (VideoCallControlService, UUID) -> Boolean) {
        val sender = FakeMeshSender(LOCAL)
        val svc = VideoCallControlService(sender)
        val callId = UUID.randomUUID()

        val ok = act(svc, callId)

        assertTrue(ok)
        assertEquals(1, sender.unicasts.size)
        val sent = sender.unicasts[0]
        assertEquals("aether:bob:02", sent.nextHopUhid)
        val body = sent.packet.payload.toString(Charsets.UTF_8)
        assertTrue(body.contains("\"action\":\"$action\""), body)
        assertTrue(body.contains("\"call_id\":\"$callId\""), body)
    }

    // ─── Handle ─────────────────────────────────────────────

    @Test fun handle_raisesCallStateChanged() = runBlocking {
        val svc = VideoCallControlService(FakeMeshSender(LOCAL))
        var got: VideoCallStateChanged? = null
        svc.onCallStateChanged = { got = it }

        val callId = UUID.randomUUID()
        val ok = svc.handle(controlPacket(callId, "ring", "aether:bob:02"))

        assertTrue(ok)
        assertNotNull(got)
        assertEquals(callId, got!!.callId)
        assertEquals("ring", got!!.action)
        assertEquals("aether:bob:02", got!!.fromUhid)
    }

    @Test fun handle_wrongPacketType_returnsFalse() {
        val svc = VideoCallControlService(FakeMeshSender(LOCAL))
        val pkt = controlPacket(UUID.randomUUID(), "ring", "aether:bob:02").apply { type = PacketType.Data }
        assertFalse(svc.handle(pkt))
    }

    @Test fun handle_malformedPayload_returnsFalse() {
        val svc = VideoCallControlService(FakeMeshSender(LOCAL))
        val pkt = MeshPacket(
            type = PacketType.VideoCall,
            sourceUhid = "aether:bob:02",
            destinationUhid = LOCAL,
            payload = "not json".toByteArray(Charsets.UTF_8),
        )
        assertFalse(svc.handle(pkt))
    }
}
