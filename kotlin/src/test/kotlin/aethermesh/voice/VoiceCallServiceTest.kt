// SPDX-License-Identifier: MIT
package aethermesh.voice

import aethermesh.FakeMeshSender
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

// ── Helpers ───────────────────────────────────────────────────────────────────

private fun makeSvc(uhid: String = "alice"): Pair<FakeMeshSender, VoiceCallService> {
    val sender = FakeMeshSender(uhid)
    return sender to VoiceCallService(sender)
}

private fun signalingPacket(from: String, json: String): MeshPacket =
    MeshPacket(
        type = PacketType.VoiceSignaling,
        sourceUhid = from,
        payload = json.toByteArray(Charsets.UTF_8)
    )

private fun offerPacket(from: String, callId: UUID): MeshPacket =
    signalingPacket(from, """{"kind":"offer","call_id":"$callId","from_uhid":"$from","to_uhid":"alice","proposed_codecs":["opus"],"sample_rate_hz":48000}""")

private fun answerPacket(from: String, callId: UUID): MeshPacket =
    signalingPacket(from, """{"kind":"answer","call_id":"$callId","from_uhid":"$from","to_uhid":"alice"}""")

private fun hangupPacket(from: String, callId: UUID, kind: String = "hangup"): MeshPacket =
    signalingPacket(from, """{"kind":"$kind","call_id":"$callId","from_uhid":"$from","to_uhid":"alice"}""")

// ── sendOffer ─────────────────────────────────────────────────────────────────

class VoiceCallServiceTest {

    @Test fun sendOffer_sendsVoiceSignalingToCallee() = runBlocking {
        val (sender, svc) = makeSvc("alice")

        val callId = svc.sendOffer("bob", listOf("opus"), 48000)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertEquals(1, toBob.size)
        assertEquals(PacketType.VoiceSignaling, toBob[0].packet.type)

        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"offer\""), "expected kind=offer")
        assertTrue(json.contains(callId.toString()), "expected callId in payload")
    }

    @Test fun sendOffer_emptyToUhid_throws() {
        val (_, svc) = makeSvc()
        var threw = false
        try {
            runBlocking { svc.sendOffer("", listOf("opus"), 48000) }
        } catch (_: IllegalArgumentException) {
            threw = true
        }
        assertTrue(threw, "expected IllegalArgumentException for empty toUhid")
    }

    // ── handlePacket — inbound offer ──────────────────────────────────────────

    @Test fun handlePacket_inboundOffer_firesOnIncomingCall() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        var session: VoiceCallSession? = null
        svc.onIncomingCall = { session = it }

        svc.handlePacket(offerPacket("bob", callId))

        assertNotNull(session, "onIncomingCall was not fired")
        assertEquals(VoiceCallState.Incoming, session?.state)
        assertEquals("bob", session?.callerUhid)
        assertEquals(callId, session?.id)
    }

    @Test fun handlePacket_inboundAnswer_transitionsToConnected() = runBlocking {
        val (sender, svc) = makeSvc("alice")

        val callId = svc.sendOffer("bob", listOf("opus"), 48000)
        sender.clear()

        var changedSession: VoiceCallSession? = null
        svc.onCallStateChanged = { changedSession = it }

        svc.handlePacket(answerPacket("bob", callId))

        assertNotNull(changedSession, "onCallStateChanged not fired")
        assertEquals(VoiceCallState.Connected, changedSession?.state)
    }

    @Test fun handlePacket_inboundHangup_firesOnCallStateChanged() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        // Set up incoming session.
        svc.handlePacket(offerPacket("bob", callId))

        var changedSession: VoiceCallSession? = null
        svc.onCallStateChanged = { changedSession = it }

        svc.handlePacket(hangupPacket("bob", callId, "hangup"))

        assertNotNull(changedSession, "onCallStateChanged not fired on hangup")
        assertEquals(VoiceCallState.Ended, changedSession?.state)
    }

    // ── acceptCall ────────────────────────────────────────────────────────────

    @Test fun acceptCall_sendsAnswerToCallerAndTransitionsToConnected() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        svc.handlePacket(offerPacket("bob", callId))
        sender.clear()

        var connected: VoiceCallSession? = null
        svc.onCallStateChanged = { connected = it }

        svc.acceptCall(callId)

        assertNotNull(connected, "onCallStateChanged not fired after acceptCall")
        assertEquals(VoiceCallState.Connected, connected?.state)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected answer unicast to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"answer\""), "expected kind=answer")
    }

    // ── hangUp ────────────────────────────────────────────────────────────────

    @Test fun hangUp_sendsCancelWhenOutgoing() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("opus"), 48000)
        sender.clear()

        svc.hangUp(callId)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty())
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"cancel\""), "outgoing call hang-up must send cancel")
    }

    @Test fun hangUp_sendsHangupWhenConnected() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("opus"), 48000)

        // Answer → Connected.
        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        svc.hangUp(callId)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty())
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"hangup\""), "connected call hang-up must send hangup")
    }

    // ── sendFrame ─────────────────────────────────────────────────────────────

    @Test fun sendFrame_activeCall_sendsVoiceCallPacket() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("opus"), 48000)

        // Answer to make Connected.
        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        svc.sendFrame(callId, byteArrayOf(1, 2, 3, 4), false)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected VoiceCall unicast to bob")
        assertEquals(PacketType.VoiceCall, toBob[0].packet.type)
        assertTrue(toBob[0].packet.payload.isNotEmpty())
    }

    @Test fun sendFrame_notConnected_noPacketSent() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("opus"), 48000)
        // Still Outgoing — not connected.
        sender.clear()

        svc.sendFrame(callId, byteArrayOf(1, 2, 3), false)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertEquals(0, toBob.size, "no VoiceCall should be sent while not connected")
    }

    // ── inbound frame ─────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundFrame_firesOnFrameReceived() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = svc.sendOffer("bob", listOf("opus"), 48000)

        svc.handlePacket(answerPacket("bob", callId))
        sender.clear()

        var gotPayload: ByteArray? = null
        svc.onFrameReceived = { _, payload, _ -> gotPayload = payload }

        // Build a minimal VoiceFrame binary payload.
        val framePkt = buildVoiceFramePacket("bob", callId)
        svc.handlePacket(framePkt)

        assertTrue(gotPayload != null, "onFrameReceived was not fired")
    }
}

// ── Binary helper for test ─────────────────────────────────────────────────────

private fun buildVoiceFramePacket(from: String, callId: UUID): MeshPacket {
    // [16] callId + [4] seq + [8] ts + [1] silence + [N] payload
    val buf = java.nio.ByteBuffer.allocate(16 + 4 + 8 + 1 + 4)
        .order(java.nio.ByteOrder.BIG_ENDIAN)
    buf.putLong(callId.mostSignificantBits)
    buf.putLong(callId.leastSignificantBits)
    buf.order(java.nio.ByteOrder.LITTLE_ENDIAN)
    buf.putInt(0)                          // sequence=0
    buf.putLong(System.currentTimeMillis()) // timestampMs
    buf.put(0)                             // isSilence=false
    buf.put(byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte(), 0xDD.toByte()))
    return MeshPacket(
        type = PacketType.VoiceCall,
        sourceUhid = from,
        payload = buf.array()
    )
}
