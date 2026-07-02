// SPDX-License-Identifier: MIT
package aethernet.identity

import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for the directed ERID-announce wire binding ([PacketType.EridAnnounce], 56)
 * plus a re-pin of the shared ERID-announcement frame byte-identity against
 * fixtures/erid/vectors.json. EridAnnounce is opaque encrypted transport — the service
 * never inspects the body; only its framing (the existing [EridAnnouncementCodec]) is
 * byte-checked. Uses the in-memory [FakeMeshSender] — no transport needed. Mirrors the C#
 * PresenceEridAnnounceTests EridAnnounce cases.
 */
class EridAnnounceServiceTest {

    // ─── EridAnnounce(56) directed transport ─────────────────

    @Test fun eridAnnounce_send_emitsDirectedPacket_andHandleRaisesEvent() = runBlocking {
        val sender = FakeMeshSender("aether:alice:01")
        val svc = EridAnnounceService(sender)
        val enc = byteArrayOf(1, 2, 3, 4, 5) // opaque Signal-encrypted announcement

        assertTrue(svc.sendAnnounce("aether:bob:02", enc))
        assertEquals(1, sender.unicasts.size)
        val sent = sender.unicasts[0]
        assertEquals(PacketType.EridAnnounce, sent.packet.type)
        assertEquals("aether:bob:02", sent.nextHopUhid)

        var gotBytes: ByteArray? = null
        var gotFrom: String? = null
        svc.onAnnounceReceived = { bytes, from -> gotBytes = bytes; gotFrom = from }
        sent.packet.sourceUhid = "aether:bob:02"
        assertTrue(svc.handle(sent.packet))
        assertNotNull(gotBytes)
        assertContentEquals(enc, gotBytes)
        assertEquals("aether:bob:02", gotFrom)
    }

    @Test fun eridAnnounce_handle_wrongTypeOrEmpty_returnsFalse() = runBlocking {
        val svc = EridAnnounceService(FakeMeshSender("aether:local:01"))
        assertFalse(svc.handle(MeshPacket(type = PacketType.Data, payload = byteArrayOf(1))))
        assertFalse(svc.handle(MeshPacket(type = PacketType.EridAnnounce, payload = ByteArray(0))))
    }

    @Test fun eridAnnounce_send_rejectsEmptyInputs() = runBlocking {
        val svc = EridAnnounceService(FakeMeshSender("aether:alice:01"))
        assertFailsWith<IllegalArgumentException> { svc.sendAnnounce("", byteArrayOf(1)) }
        assertFailsWith<IllegalArgumentException> { svc.sendAnnounce("aether:bob:02", ByteArray(0)) }
        Unit // keep the @Test method Unit-returning so JUnit 5 collects it
    }

    // ─── Re-pin: shared ERID-announcement frame (fixtures/erid/vectors.json) ─────
    // The existing 8/8 codec is byte-identical with the C# reference; re-assert it here so
    // the announce transport and its framing are pinned together.

    private fun repoRoot(): File {
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "AetherNetProtocol.slnx")
            if (candidate.exists()) return dir!!
            dir = dir?.parentFile ?: return@repeat
        }
        throw IllegalStateException("AetherNetProtocol.slnx not found from ${File(".").canonicalFile}")
    }

    private fun hex(b: ByteArray): String = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    private fun fromHex(s: String): ByteArray =
        ByteArray(s.length / 2) { ((s.substring(it * 2, it * 2 + 2)).toInt(16)).toByte() }

    @Test fun eridAnnouncementCodec_matchesCanonicalFrame() {
        val v = JSONObject(File(repoRoot(), "fixtures/erid/vectors.json").readText())
        val routingKey = fromHex(v.getString("routing_key_hex"))
        val frame = EridAnnouncementCodec.encode(
            routingKey,
            epochSeconds = v.getInt("epoch_seconds"),
            eridLength = v.getInt("erid_length"),
        )
        assertEquals(v.getString("announcement_encode_hex"), hex(frame))
    }
}
