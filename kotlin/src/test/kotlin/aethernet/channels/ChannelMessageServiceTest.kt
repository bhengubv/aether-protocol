// SPDX-License-Identifier: MIT
package aethernet.channels

import aethernet.AetherNetConstants
import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for [ChannelMessageService] (PacketType.ChannelMessage). Uses the in-memory
 * [FakeMeshSender] — no transport needed. Mirrors the C# ChannelMessageTests.
 */
private const val LOCAL = "aether:local:01"

private data class ChSvc(val svc: ChannelMessageService, val sender: FakeMeshSender)

private fun newSvc(localUhid: String = LOCAL): ChSvc {
    val sender = FakeMeshSender(localUhid)
    return ChSvc(ChannelMessageService(sender), sender)
}

private fun channelPacket(
    channelId: String,
    messageId: UUID,
    sender: String,
    content: String,
    sentAtMs: Long,
    ttl: Int = 7
): MeshPacket = MeshPacket(
    type = PacketType.ChannelMessage,
    sourceUhid = sender,
    destinationUhid = "*",
    ttl = ttl,
    payload = ChannelMessagePayload(
        channelId = channelId,
        messageId = messageId,
        senderUhid = sender,
        content = content,
        sentAtMs = sentAtMs
    ).toJsonBytes(),
)

class ChannelMessageServiceTest {

    // ─── Byte-identity gate (fixtures/channels/vectors.json) ─────
    // snake_case, field order channel_id, message_id, sender_uhid, content, sent_at_ms, no whitespace,
    // lowercase-dashed UUID, sent_at_ms a bare integer. Must be byte-identical with C# in every port.

    @Test fun channelMessagePayload_serializesToCanonicalBytes_vector1() {
        val json = ChannelMessagePayload(
            channelId = "res-floor-3",
            messageId = UUID.fromString("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
            senderUhid = "aether:alice:01",
            content = "meeting at 6",
            sentAtMs = 1_700_000_000_000L
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"channel_id\":\"res-floor-3\",\"message_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"sender_uhid\":\"aether:alice:01\",\"content\":\"meeting at 6\",\"sent_at_ms\":1700000000000}",
            json
        )
    }

    @Test fun channelMessagePayload_serializesToCanonicalBytes_vector2() {
        val json = ChannelMessagePayload(
            channelId = "g",
            messageId = UUID.fromString("00000000-0000-0000-0000-000000000000"),
            senderUhid = "n",
            content = "",
            sentAtMs = 0L
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"channel_id\":\"g\",\"message_id\":\"00000000-0000-0000-0000-000000000000\",\"sender_uhid\":\"n\",\"content\":\"\",\"sent_at_ms\":0}",
            json
        )
    }

    // ─── Publish ────────────────────────────────────────────

    @Test fun publish_broadcastsChannelMessage() = runBlocking {
        val (svc, sender) = newSvc("aether:alice:01")

        svc.publish("res-floor-3", "meeting at 6")

        assertEquals(1, sender.broadcasts.size)
        val pkt = sender.broadcasts[0]
        assertEquals(PacketType.ChannelMessage, pkt.type)
        assertEquals("*", pkt.destinationUhid)
        assertEquals(AetherNetConstants.DEFAULT_TTL, pkt.ttl)
        val body = pkt.payload.toString(Charsets.UTF_8)
        assertTrue(body.contains("\"channel_id\":\"res-floor-3\""), body)
        assertTrue(body.contains("\"content\":\"meeting at 6\""), body)
        assertTrue(body.contains("\"sender_uhid\":\"aether:alice:01\""), body)
    }

    @Test fun publish_returnsDeliveredPeerCount() = runBlocking {
        val sender = FakeMeshSender(LOCAL)
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:aa", identityKey = ByteArray(0)))
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:bb", identityKey = ByteArray(0)))
        val svc = ChannelMessageService(sender)

        assertEquals(2, svc.publish("res-floor-3", "hi"))
    }

    // ─── Subscriptions ──────────────────────────────────────

    @Test fun subscribe_unsubscribe_tracksSubscriptions() {
        val (svc, _) = newSvc()
        svc.subscribe("res-floor-3")
        svc.subscribe("society-x")
        assertEquals(setOf("res-floor-3", "society-x"), svc.getSubscriptions().toSet())

        svc.unsubscribe("res-floor-3")
        assertEquals(setOf("society-x"), svc.getSubscriptions().toSet())
    }

    // ─── Handle ─────────────────────────────────────────────

    @Test fun handle_subscribedChannel_raisesEvent() = runBlocking {
        val (svc, _) = newSvc()
        svc.subscribe("res-floor-3")

        var got: ChannelMessagePayload? = null
        svc.onMessageReceived = { got = it }

        val ok = svc.handle(
            channelPacket("res-floor-3", UUID.randomUUID(), "aether:bob:02", "hello floor", 1_700_000_000_000L)
        )

        assertTrue(ok)
        assertNotNull(got)
        assertEquals("res-floor-3", got!!.channelId)
        assertEquals("hello floor", got!!.content)
        assertEquals("aether:bob:02", got!!.senderUhid)
        assertEquals(1_700_000_000_000L, got!!.sentAtMs)
    }

    @Test fun handle_unsubscribedChannel_noEventButProcessed() = runBlocking {
        val (svc, _) = newSvc()
        var raised = false
        svc.onMessageReceived = { raised = true }

        val ok = svc.handle(
            channelPacket("society-x", UUID.randomUUID(), "aether:bob:02", "hi", 1L)
        )

        assertTrue(ok)      // processed + relayed
        assertFalse(raised) // but not surfaced — we aren't subscribed
    }

    @Test fun handle_ownMessage_noEvent() = runBlocking {
        val (svc, sender) = newSvc(LOCAL)
        svc.subscribe("res-floor-3")
        var raised = false
        svc.onMessageReceived = { raised = true }

        // A message whose sender_uhid is us — even if subscribed, it must not surface, and must not relay.
        val ok = svc.handle(
            channelPacket("res-floor-3", UUID.randomUUID(), LOCAL, "mine", 1L, ttl = 5)
        )

        assertTrue(ok)
        assertFalse(raised)
        assertEquals(0, sender.broadcasts.size)
    }

    @Test fun handle_duplicateMessageId_returnsFalse() = runBlocking {
        val (svc, _) = newSvc()
        svc.subscribe("res-floor-3")
        val id = UUID.randomUUID()

        var events = 0
        svc.onMessageReceived = { events++ }

        assertTrue(svc.handle(channelPacket("res-floor-3", id, "aether:bob:02", "one", 1L)))
        assertFalse(svc.handle(channelPacket("res-floor-3", id, "aether:bob:02", "one", 1L)))
        assertEquals(1, events)
    }

    @Test fun handle_wrongPacketType_returnsFalse() = runBlocking {
        val (svc, _) = newSvc()
        val pkt = channelPacket("res-floor-3", UUID.randomUUID(), "aether:bob:02", "x", 1L)
            .apply { type = PacketType.Data }
        assertFalse(svc.handle(pkt))
    }

    @Test fun handle_malformedPayload_returnsFalse() = runBlocking {
        val (svc, _) = newSvc()
        val pkt = MeshPacket(
            type = PacketType.ChannelMessage,
            sourceUhid = "aether:bob:02",
            destinationUhid = "*",
            payload = "not json".toByteArray(Charsets.UTF_8),
        )
        assertFalse(svc.handle(pkt))
    }

    @Test fun handle_relaysWhenTtlAllows() = runBlocking {
        // Not subscribed — pure relay.
        val relaySender = FakeMeshSender("aether:relay:09")
        val svc = ChannelMessageService(relaySender)

        svc.handle(channelPacket("res-floor-3", UUID.randomUUID(), "aether:bob:02", "hop", 1L, ttl = 5))

        assertEquals(1, relaySender.broadcasts.size)
        val relayed = relaySender.broadcasts[0]
        assertEquals(PacketType.ChannelMessage, relayed.type)
        assertEquals(4, relayed.ttl)
    }

    @Test fun handle_doesNotRelayWhenTtlExhausted() = runBlocking {
        val relaySender = FakeMeshSender("aether:relay:09")
        val svc = ChannelMessageService(relaySender)

        svc.handle(channelPacket("res-floor-3", UUID.randomUUID(), "aether:bob:02", "last", 1L, ttl = 1))

        assertEquals(0, relaySender.broadcasts.size)
    }
}
