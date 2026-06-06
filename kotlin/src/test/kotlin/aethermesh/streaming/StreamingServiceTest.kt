// SPDX-License-Identifier: MIT
package aethermesh.streaming

import aethermesh.FakeMeshSender
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

// ── Helpers ───────────────────────────────────────────────────────────────────

private fun makeSvc(uhid: String = "alice"): Pair<FakeMeshSender, StreamingService> {
    val sender = FakeMeshSender(uhid)
    return sender to StreamingService(sender)
}

private fun jsonPayload(vararg pairs: Pair<String, String>): ByteArray {
    val body = pairs.joinToString(",") { (k, v) ->
        "\"$k\":\"$v\""
    }
    return "{$body}".toByteArray(Charsets.UTF_8)
}

private fun subscribePacket(from: String, to: String, streamId: UUID): MeshPacket =
    MeshPacket(
        type = PacketType.StreamSubscribe,
        sourceUhid = from,
        destinationUhid = to,
        payload = """{"stream_id":"$streamId","live_only":false}""".toByteArray()
    )

private fun unsubscribePacket(from: String, to: String, streamId: UUID): MeshPacket =
    MeshPacket(
        type = PacketType.StreamUnsubscribe,
        sourceUhid = from,
        destinationUhid = to,
        payload = """{"stream_id":"$streamId"}""".toByteArray()
    )

private fun announcePacket(from: String, streamId: UUID, state: String, title: String = "test"): MeshPacket =
    MeshPacket(
        type = PacketType.StreamAnnounce,
        sourceUhid = from,
        payload = """{"stream_id":"$streamId","title":"$title","content_type":"video/h264","codec":"h264","segment_duration_ms":1000,"state":"$state","started_at_ms":0}""".toByteArray()
    )

// ── startStream ───────────────────────────────────────────────────────────────

class StreamingServiceTest {

    @Test fun startStream_broadcastsStreamAnnounce() = runBlocking {
        val (sender, svc) = makeSvc()

        val streamId = svc.startStream("My Stream", "video/h264", "h264", 2000)

        assertEquals(1, sender.broadcasts.size)
        val pkt = sender.broadcasts[0]
        assertEquals(PacketType.StreamAnnounce, pkt.type)

        val json = String(pkt.payload, Charsets.UTF_8)
        assertTrue(json.contains("live"), "expected state=live in payload")
        assertTrue(json.contains("My Stream"), "expected title in payload")
        assertTrue(json.contains(streamId.toString()), "expected streamId in payload")
    }

    @Test fun startStream_emptyTitle_throws() {
        val (_, svc) = makeSvc()
        var threw = false
        try {
            runBlocking { svc.startStream("", "video/h264", "h264", 1000) }
        } catch (_: IllegalArgumentException) {
            threw = true
        }
        assertTrue(threw, "expected IllegalArgumentException for empty title")
    }

    // ── endStream ─────────────────────────────────────────────────────────────

    @Test fun endStream_broadcastsEndedAnnounce() = runBlocking {
        val (sender, svc) = makeSvc()
        val streamId = svc.startStream("T", "video/h264", "h264", 1000)
        sender.clear()

        svc.endStream(streamId)

        assertEquals(1, sender.broadcasts.size)
        val json = String(sender.broadcasts[0].payload, Charsets.UTF_8)
        assertTrue(json.contains("ended"), "expected state=ended")
    }

    @Test fun endStream_firesOnStreamEnded() = runBlocking {
        val (_, svc) = makeSvc()
        val streamId = svc.startStream("T", "video/h264", "h264", 1000)

        var endedId: UUID? = null
        svc.onStreamEnded = { endedId = it }
        svc.endStream(streamId)

        assertEquals(streamId, endedId)
    }

    // ── subscribe / unsubscribe ───────────────────────────────────────────────

    @Test fun subscribe_sendsSubscribePacketToPublisher() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val fakeStreamId = UUID.randomUUID()

        svc.subscribe(fakeStreamId, "bob", false)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertEquals(1, toBob.size)
        assertEquals(PacketType.StreamSubscribe, toBob[0].packet.type)
    }

    @Test fun unsubscribe_sendsUnsubscribePacketToPublisher() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val fakeStreamId = UUID.randomUUID()

        svc.subscribe(fakeStreamId, "bob", false)
        sender.clear()
        svc.unsubscribe(fakeStreamId, "bob")

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertEquals(1, toBob.size)
        assertEquals(PacketType.StreamUnsubscribe, toBob[0].packet.type)
    }

    // ── handlePacket — subscribe flow ─────────────────────────────────────────

    @Test fun handlePacket_subscribe_thenPublishSegment_reachesSubscriber() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val streamId = svc.startStream("T", "video/h264", "h264", 1000)
        sender.clear()

        svc.handlePacket(subscribePacket("bob", "alice", streamId))
        svc.publishSegment(streamId, byteArrayOf(1, 2, 3, 4), true)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected StreamSegment unicast to bob")
        assertEquals(PacketType.StreamSegment, toBob[0].packet.type)
    }

    @Test fun handlePacket_unsubscribe_stopsSegmentDelivery() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val streamId = svc.startStream("T", "video/h264", "h264", 1000)

        svc.handlePacket(subscribePacket("bob", "alice", streamId))
        svc.handlePacket(unsubscribePacket("bob", "alice", streamId))
        sender.clear()

        svc.publishSegment(streamId, byteArrayOf(1, 2, 3), false)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertEquals(0, toBob.size, "unsubscribed bob must not receive segments")
    }

    @Test fun publishSegment_fansOutToMultipleSubscribers() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val streamId = svc.startStream("T", "video/h264", "h264", 1000)

        svc.handlePacket(subscribePacket("bob", "alice", streamId))
        svc.handlePacket(subscribePacket("carol", "alice", streamId))
        sender.clear()

        svc.publishSegment(streamId, byteArrayOf(1, 2, 3), false)

        assertTrue(sender.unicasts.any { it.nextHopUhid == "bob" }, "bob should receive segment")
        assertTrue(sender.unicasts.any { it.nextHopUhid == "carol" }, "carol should receive segment")
    }

    // ── handlePacket — announce flow ──────────────────────────────────────────

    @Test fun handlePacket_liveAnnounce_firesOnStreamAnnounced() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val remoteStreamId = UUID.randomUUID()

        var announcedInfo: StreamInfo? = null
        svc.onStreamAnnounced = { announcedInfo = it }

        svc.handlePacket(announcePacket("bob", remoteStreamId, "live", "Bob's Stream"))

        assertNotNull(announcedInfo)
        assertEquals("Bob's Stream", announcedInfo?.title)
        assertEquals("bob", announcedInfo?.publisherUhid)
    }

    @Test fun handlePacket_endedAnnounce_firesOnStreamEnded() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val remoteStreamId = UUID.randomUUID()

        var endedId: UUID? = null
        svc.onStreamEnded = { endedId = it }

        svc.handlePacket(announcePacket("bob", remoteStreamId, "ended"))

        assertEquals(remoteStreamId, endedId)
    }
}
