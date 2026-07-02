// SPDX-License-Identifier: MIT

package aethernet.media

import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for the VoicePtt(15) + ScreenShare(32) media-frame bindings. Binary frames sharing the
 * 29-byte header (call_id big-endian, sequence/timestamp little-endian, flag). Byte-identity gates
 * (fixtures/media/vectors.json expected_hex) + send/handle behaviour. Mirrors the C#
 * MediaFrameTests. Uses the in-memory [FakeMeshSender] — no transport needed.
 */
class MediaFrameTest {

    private val callId: UUID = UUID.fromString("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f")

    private fun hex(b: ByteArray): String = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    // ── Byte-identity gates ─────────────────────────────────────────────────

    @Test fun voicePtt_frame_serializesToCanonicalBytes() {
        val f = VoicePttFrame(callId, 42u, 1700000000000L, isSilence = false, encodedPayload = byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte()))
        assertEquals(
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2a0000000068e5cf8b01000000aabbcc",
            hex(MediaFrameCodec.serializeVoicePtt(f)),
        )
    }

    @Test fun voicePtt_silenceEmpty_serializesToCanonicalBytes() {
        val f = VoicePttFrame(callId, 43u, 1700000000020L, isSilence = true, encodedPayload = byteArrayOf())
        assertEquals(
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2b0000001468e5cf8b01000001",
            hex(MediaFrameCodec.serializeVoicePtt(f)),
        )
    }

    @Test fun screenShare_keyframe_serializesToCanonicalBytes() {
        val f = ScreenShareFrame(callId, 7u, 1700000000000L, isKeyframe = true, encodedPayload = byteArrayOf(0x11, 0x22, 0x33, 0x44))
        assertEquals(
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f070000000068e5cf8b0100000111223344",
            hex(MediaFrameCodec.serializeScreenShare(f)),
        )
    }

    @Test fun screenShare_deltaEmpty_serializesToCanonicalBytes() {
        val f = ScreenShareFrame(UUID(0L, 0L), 0u, 0L, isKeyframe = false, encodedPayload = byteArrayOf())
        assertEquals(
            "0000000000000000000000000000000000000000000000000000000000",
            hex(MediaFrameCodec.serializeScreenShare(f)),
        )
    }

    @Test fun voicePtt_roundTrips() {
        val f = VoicePttFrame(callId, 99u, 123456789L, isSilence = true, encodedPayload = byteArrayOf(1, 2, 3, 4, 5))
        val back = MediaFrameCodec.deserializeVoicePtt(MediaFrameCodec.serializeVoicePtt(f))
        assertEquals(callId, back.callId)
        assertEquals(99u, back.sequence)
        assertEquals(123456789L, back.timestampMs)
        assertTrue(back.isSilence)
        assertContentEquals(f.encodedPayload, back.encodedPayload)
    }

    @Test fun screenShare_roundTrips_keyframeAndCallIdBigEndian() {
        val f = ScreenShareFrame(callId, 5u, 999L, isKeyframe = true, encodedPayload = byteArrayOf(0xFF.toByte()))
        val back = MediaFrameCodec.deserializeScreenShare(MediaFrameCodec.serializeScreenShare(f))
        assertEquals(callId, back.callId)
        assertTrue(back.isKeyframe)
        assertContentEquals(byteArrayOf(0xFF.toByte()), back.encodedPayload)
    }

    // ── Behaviour ───────────────────────────────────────────────────────────

    @Test fun voicePtt_send_emitsDirectedFrame_andHandleRaisesEvent() = runBlocking {
        val s = FakeMeshSender("aether:alice:01")
        val svc = VoicePttService(s)
        val frame = VoicePttFrame(callId, 42u, 1700000000000L, encodedPayload = byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte()))

        assertTrue(svc.sendFrame("aether:bob:02", frame))
        assertEquals(1, s.unicasts.size)
        val sent = s.unicasts[0]
        assertEquals(PacketType.VoicePtt, sent.packet.type)
        assertEquals("aether:bob:02", sent.nextHopUhid)

        var got: VoicePttFrameReceived? = null
        svc.onFrameReceived = { e -> got = e }
        sent.packet.sourceUhid = "aether:alice:01"
        assertTrue(svc.handle(sent.packet))
        assertNotNull(got)
        assertEquals(42u, got!!.frame.sequence)
        assertEquals("aether:alice:01", got!!.fromUhid)
        assertContentEquals(byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte()), got!!.frame.encodedPayload)
    }

    @Test fun screenShare_send_emitsDirectedFrame_andHandleRaisesEvent() = runBlocking {
        val s = FakeMeshSender("aether:alice:01")
        val svc = ScreenShareService(s)
        val frame = ScreenShareFrame(callId, 7u, 1700000000000L, isKeyframe = true, encodedPayload = byteArrayOf(0x11, 0x22, 0x33, 0x44))

        assertTrue(svc.sendFrame("aether:bob:02", frame))
        assertEquals(1, s.unicasts.size)
        val sent = s.unicasts[0]
        assertEquals(PacketType.ScreenShare, sent.packet.type)

        var got: ScreenShareFrameReceived? = null
        svc.onFrameReceived = { e -> got = e }
        assertTrue(svc.handle(sent.packet))
        assertNotNull(got)
        assertTrue(got!!.frame.isKeyframe)
        assertEquals(7u, got!!.frame.sequence)
    }

    @Test fun handle_wrongType_returnsFalse() = runBlocking {
        val vp = VoicePttService(FakeMeshSender("aether:local:01"))
        val ss = ScreenShareService(FakeMeshSender("aether:local:01"))
        assertFalse(vp.handle(MeshPacket(type = PacketType.Data, payload = ByteArray(40))))
        assertFalse(ss.handle(MeshPacket(type = PacketType.Data, payload = ByteArray(40))))
    }

    @Test fun handle_shortFrame_returnsFalse() = runBlocking {
        val vp = VoicePttService(FakeMeshSender("aether:local:01"))
        assertFalse(vp.handle(MeshPacket(type = PacketType.VoicePtt, payload = ByteArray(10))))
    }
}
