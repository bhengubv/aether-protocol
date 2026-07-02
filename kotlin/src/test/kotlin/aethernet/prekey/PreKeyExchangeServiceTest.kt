// SPDX-License-Identifier: MIT
package aethernet.prekey

import aethernet.AetherNetConstants
import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.security.PreKeyBundle
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Unit tests for [PreKeyExchangeService] (PacketType.PreKeyRequest 25 / PreKeyResponse 26). Directed
 * request/response transport of a [PreKeyBundle] over the mesh. Uses the in-memory [FakeMeshSender] —
 * no transport needed. Mirrors the C# PreKeyExchangeTests.
 */
private const val LOCAL = "aether:local:01"

/** Constant-byte-fill sample bundle — matches the C# SampleBundle so a field swap is caught. */
private fun sampleBundle(uhid: String = "aether:bob:02"): PreKeyBundle = PreKeyBundle(
    uhid = uhid,
    identityKey = ByteArray(32) { 0x11 },
    identityKeyX25519 = ByteArray(32) { 0x22 },
    preKeyId = 4242,
    preKey = ByteArray(32) { 0x33 },
    signedPreKeyId = 77,
    signedPreKey = ByteArray(32) { 0x44 },
    signedPreKeySignature = ByteArray(64) { 0x55 }
)

class PreKeyExchangeServiceTest {

    // ─── Byte-identity gate (fixtures/prekey/vectors.json) ─────
    // STANDARD base64, field order pinned, no whitespace, lowercase-dashed UUID, bare-int ids.
    // Must be byte-identical with C# in every port.

    @Test fun requestPayload_serializesToCanonicalBytes() {
        val json = PreKeyRequestPayload(
            requestId = UUID.fromString("11112222-3333-4444-5555-666677778888"),
            requesterUhid = "aether:alice:01"
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"request_id\":\"11112222-3333-4444-5555-666677778888\",\"requester_uhid\":\"aether:alice:01\"}",
            json
        )
    }

    @Test fun responsePayload_serializesToCanonicalBytes() {
        val json = PreKeyResponsePayload.fromBundle(
            UUID.fromString("7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a"),
            sampleBundle()
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"request_id\":\"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a\",\"uhid\":\"aether:bob:02\"," +
                "\"identity_key\":\"ERERERERERERERERERERERERERERERERERERERERERE=\"," +
                "\"identity_key_x25519\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\"," +
                "\"pre_key_id\":4242,\"pre_key\":\"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=\"," +
                "\"signed_pre_key_id\":77,\"signed_pre_key\":\"REREREREREREREREREREREREREREREREREREREREREQ=\"," +
                "\"signed_pre_key_signature\":\"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==\"}",
            json
        )
    }

    @Test fun responsePayload_roundTripsThroughBundle() {
        val original = sampleBundle()
        val payload = PreKeyResponsePayload.fromBundle(UUID.randomUUID(), original)
        val back = payload.toBundle()
        assertEquals(original.uhid, back.uhid)
        assertEquals(original.preKeyId, back.preKeyId)
        assertEquals(original.signedPreKeyId, back.signedPreKeyId)
        assertTrue(original.identityKey.contentEquals(back.identityKey))
        assertTrue(original.signedPreKeySignature.contentEquals(back.signedPreKeySignature))
    }

    // ─── Behaviour ─────────────────────────────────────────────

    @Test fun request_sendsDirectedPreKeyRequest_andReturnsId() = runBlocking {
        val sender = FakeMeshSender("aether:alice:01")
        val svc = PreKeyExchangeService(sender)

        val reqId = svc.requestBundle("aether:bob:02")

        assertNotEquals(UUID(0L, 0L), reqId)
        assertEquals(1, sender.unicasts.size)
        val sent = sender.unicasts[0]
        assertEquals(PacketType.PreKeyRequest, sent.packet.type)
        assertEquals("aether:bob:02", sent.nextHopUhid)
        assertEquals(AetherNetConstants.DEFAULT_TTL, sent.packet.ttl)
        val body = PreKeyRequestPayload.fromJson(sent.packet.payload.toString(Charsets.UTF_8))!!
        assertEquals(reqId, body.requestId)
        assertEquals("aether:alice:01", body.requesterUhid)
    }

    @Test fun handleRequest_withLocalBundle_sendsDirectedResponseToRequester() = runBlocking {
        val sender = FakeMeshSender("aether:bob:02")
        val svc = PreKeyExchangeService(sender)
        svc.setLocalBundle(sampleBundle("aether:bob:02"))

        val reqId = UUID.randomUUID()
        val reqPkt = MeshPacket(
            type = PacketType.PreKeyRequest,
            sourceUhid = "aether:alice:01",
            destinationUhid = "aether:bob:02",
            payload = PreKeyRequestPayload(reqId, "aether:alice:01").toJsonBytes()
        )

        assertTrue(svc.handle(reqPkt))
        assertEquals(1, sender.unicasts.size)
        val sent = sender.unicasts[0]
        assertEquals(PacketType.PreKeyResponse, sent.packet.type)
        assertEquals("aether:alice:01", sent.nextHopUhid)
        val body = PreKeyResponsePayload.fromJson(sent.packet.payload.toString(Charsets.UTF_8))!!
        assertEquals(reqId, body.requestId)
        assertEquals("aether:bob:02", body.uhid)
        assertEquals(4242, body.preKeyId)
        assertEquals(64, body.signedPreKeySignature.size)
    }

    @Test fun handleRequest_noLocalBundle_returnsFalse_andSendsNothing() = runBlocking {
        val sender = FakeMeshSender(LOCAL)
        val svc = PreKeyExchangeService(sender)
        val reqPkt = MeshPacket(
            type = PacketType.PreKeyRequest,
            sourceUhid = "aether:alice:01",
            payload = PreKeyRequestPayload(UUID.randomUUID(), "aether:alice:01").toJsonBytes()
        )

        assertFalse(svc.handle(reqPkt))
        assertEquals(0, sender.unicasts.size)
    }

    @Test fun handleResponse_cachesBundle_andRaisesEvent() = runBlocking {
        val sender = FakeMeshSender("aether:alice:01")
        val svc = PreKeyExchangeService(sender)
        var got: PreKeyBundleReceived? = null
        svc.onBundleReceived = { got = it }

        val reqId = UUID.randomUUID()
        val respPkt = MeshPacket(
            type = PacketType.PreKeyResponse,
            sourceUhid = "aether:bob:02",
            destinationUhid = "aether:alice:01",
            payload = PreKeyResponsePayload.fromBundle(reqId, sampleBundle("aether:bob:02")).toJsonBytes()
        )

        assertTrue(svc.handle(respPkt))
        assertNotNull(got)
        assertEquals(reqId, got!!.requestId)
        assertEquals("aether:bob:02", got!!.fromUhid)
        assertEquals("aether:bob:02", got!!.bundle.uhid)

        val cached = svc.getReceivedBundle("aether:bob:02")
        assertNotNull(cached)
        assertEquals(4242, cached!!.preKeyId)
    }

    @Test fun handle_wrongPacketType_returnsFalse() = runBlocking {
        val svc = PreKeyExchangeService(FakeMeshSender(LOCAL))
        val pkt = MeshPacket(type = PacketType.Data, sourceUhid = "aether:x:01", payload = ByteArray(0))
        assertFalse(svc.handle(pkt))
        assertNull(svc.getReceivedBundle("aether:x:01"))
    }
}
