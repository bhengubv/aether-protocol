// SPDX-License-Identifier: MIT
package aethernet.streaming

import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertTrue

// ── Helpers ───────────────────────────────────────────────────────────────────

private fun makeSvc(uhid: String = "alice"): Pair<FakeMeshSender, VideoCallService> {
    val sender = FakeMeshSender(uhid)
    return sender to VideoCallService(sender)
}

private fun videoSignalingPacket(from: String, json: String): MeshPacket =
    MeshPacket(
        type = PacketType.VideoSignaling,
        sourceUhid = from,
        payload = json.toByteArray(Charsets.UTF_8)
    )

private fun offerPacket(from: String, callId: UUID): MeshPacket =
    videoSignalingPacket(
        from,
        """{"kind":"offer","call_id":"$callId","from_uhid":"$from","to_uhid":"alice","proposed_codecs":["h264"],"width":1280,"height":720,"fps":30,"bitrate_kbps":2000}"""
    )

private fun answerPacket(from: String, callId: UUID): MeshPacket =
    videoSignalingPacket(
        from,
        """{"kind":"answer","call_id":"$callId","from_uhid":"$from","to_uhid":"alice","selected_codec":"h264","width":1280,"height":720,"fps":30,"bitrate_kbps":2000}"""
    )

private fun hangupPacket(from: String, callId: UUID, kind: String = "hangup"): MeshPacket =
    videoSignalingPacket(
        from,
        """{"kind":"$kind","call_id":"$callId","from_uhid":"$from","to_uhid":"alice"}"""
    )

private fun buildVideoFramePacket(from: String, callId: UUID): MeshPacket {
    // [16] callId + [4] seq + [8] ts + [1] isKeyframe + [N] payload
    val video = byteArrayOf(0x11, 0x22, 0x33, 0x44)
    val buf = ByteBuffer.allocate(16 + 4 + 8 + 1 + video.size)
    buf.order(ByteOrder.BIG_ENDIAN)
    buf.putLong(callId.mostSignificantBits)
    buf.putLong(callId.leastSignificantBits)
    buf.order(ByteOrder.LITTLE_ENDIAN)
    buf.putInt(0)                           // sequence
    buf.putLong(System.currentTimeMillis()) // timestampMs
    buf.put(1)                              // isKeyframe=true
    buf.put(video)
    return MeshPacket(
        type = PacketType.VideoFrame,
        sourceUhid = from,
        payload = buf.array()
    )
}

// ── sendOffer ─────────────────────────────────────────────────────────────────

class VideoCallServiceTest {

    @Test fun sendOffer_sendsVideoSignalingToCallee() = runBlocking {
        val (sender, svc) = makeSvc("alice")

        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertEquals(1, toBob.size)
        assertEquals(PacketType.VideoSignaling, toBob[0].packet.type)

        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"offer\""), "expected kind=offer")
        assertTrue(json.contains(callId.toString()), "expected callId in payload")
    }

    @Test fun sendOffer_emptyToUhid_throws() {
        val (_, svc) = makeSvc()
        var threw = false
        try {
            runBlocking { svc.sendOffer("", listOf("h264"), 1280, 720, 30, 2000) }
        } catch (_: IllegalArgumentException) {
            threw = true
        }
        assertTrue(threw, "expected IllegalArgumentException for empty toUhid")
    }

    // ── inbound offer ─────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundOffer_firesOnIncomingCall() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        var session: VideoCallSession? = null
        svc.onIncomingCall = { session = it }

        svc.handlePacket(offerPacket("bob", callId))

        assertTrue(session != null, "onIncomingCall was not fired")
        assertEquals(VideoCallState.Incoming, session?.state)
        assertEquals("bob", session?.callerUhid)
        assertEquals(callId, session?.id)
    }

    // ── inbound answer ────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundAnswer_transitionsToConnected() = runBlocking {
        val (sender, svc) = makeSvc("alice")

        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)
        sender.clear()

        var changedSession: VideoCallSession? = null
        svc.onCallStateChanged = { changedSession = it }

        svc.handlePacket(answerPacket("bob", callId))

        assertTrue(changedSession != null, "onCallStateChanged not fired")
        assertEquals(VideoCallState.Connected, changedSession?.state)
    }

    // ── acceptCall ────────────────────────────────────────────────────────────

    @Test fun acceptCall_sendsAnswerToCallerAndTransitionsToConnected() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        svc.handlePacket(offerPacket("bob", callId))
        sender.clear()

        var connected: VideoCallSession? = null
        svc.onCallStateChanged = { connected = it }

        svc.acceptCall(callId)

        assertTrue(connected != null, "onCallStateChanged not fired after acceptCall")
        assertEquals(VideoCallState.Connected, connected?.state)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected answer unicast to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"answer\""), "expected kind=answer")
    }

    // ── hangUp ────────────────────────────────────────────────────────────────

    @Test fun hangUp_sendsCancelWhenOutgoing() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)
        sender.clear()

        svc.hangUp(callId)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty())
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"cancel\""), "outgoing call hang-up must send cancel")
    }

    @Test fun hangUp_sendsHangupWhenConnected() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)

        // Answer → Connected
        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        svc.hangUp(callId)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty())
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"hangup\""), "connected call hang-up must send hangup")
    }

    // ── sendFrame ─────────────────────────────────────────────────────────────

    @Test fun sendFrame_activeCall_sendsVideoFramePacket() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)

        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        svc.sendFrame(callId, byteArrayOf(1, 2, 3, 4), isKeyframe = true)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected VideoFrame unicast to bob")
        assertEquals(PacketType.VideoFrame, toBob[0].packet.type)
        assertTrue(toBob[0].packet.payload.isNotEmpty())
    }

    @Test fun sendFrame_notConnected_noPacketSent() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)
        // Still Outgoing — not connected
        sender.clear()

        svc.sendFrame(callId, byteArrayOf(1, 2, 3), isKeyframe = false)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertEquals(0, toBob.size, "no VideoFrame should be sent while not connected")
    }

    // ── inbound frame ─────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundFrame_firesOnFrameReceived() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)

        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        var gotCallId: UUID? = null
        var gotKeyframe: Boolean? = null
        svc.onFrameReceived = { cid, _, kf -> gotCallId = cid; gotKeyframe = kf }

        svc.handlePacket(buildVideoFramePacket("bob", callId))

        assertTrue(gotCallId != null, "onFrameReceived was not fired")
        assertEquals(callId, gotCallId)
        assertEquals(true, gotKeyframe)
    }

    // ── requestKeyframe ───────────────────────────────────────────────────────

    @Test fun requestKeyframe_sendsKeyframeRequest() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)
        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        svc.requestKeyframe(callId)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected keyframe_request unicast to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("keyframe_request"), "expected keyframe_request in payload")
    }

    @Test fun handlePacket_keyframeRequest_firesOnKeyframeRequested() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)
        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        var firedCallId: UUID? = null
        svc.onKeyframeRequested = { cid -> firedCallId = cid }

        svc.handlePacket(videoSignalingPacket("bob",
            """{"kind":"keyframe_request","call_id":"$callId","from_uhid":"bob","to_uhid":"alice"}"""))

        assertTrue(firedCallId != null, "onKeyframeRequested was not fired")
        assertEquals(callId, firedCallId)
    }

    // ── notifyQualityChange ───────────────────────────────────────────────────

    @Test fun notifyQualityChange_sendsQualityChangeSignaling() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)
        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        svc.notifyQualityChange(callId, 640, 360, 15, 500)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected quality_change unicast to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("quality_change"), "expected quality_change in payload")
        assertTrue(json.contains("640"), "expected new width in payload")
    }

    @Test fun handlePacket_qualityChange_firesOnQualityChanged() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("h264"), 1280, 720, 30, 2000)
        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        var gotW = 0; var gotH = 0; var gotFps = 0; var gotBitrate = 0
        svc.onQualityChanged = { _, w, h, f, b -> gotW = w; gotH = h; gotFps = f; gotBitrate = b }

        svc.handlePacket(videoSignalingPacket("bob",
            """{"kind":"quality_change","call_id":"$callId","from_uhid":"bob","to_uhid":"alice","width":640,"height":360,"fps":15,"bitrate_kbps":500}"""))

        assertEquals(640, gotW)
        assertEquals(360, gotH)
        assertEquals(15, gotFps)
        assertEquals(500, gotBitrate)
    }
}
