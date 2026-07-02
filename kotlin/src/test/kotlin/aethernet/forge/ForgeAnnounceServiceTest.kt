// SPDX-License-Identifier: MIT
package aethernet.forge

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
 * Unit tests for the ForgeAnnounce wire binding ([PacketType.ForgeAnnounce], 41).
 * Byte-identity gate (fixtures/forge/vectors.json) + broadcast/handle behaviour.
 * Uses the in-memory [FakeMeshSender] — no transport needed. Mirrors the C#
 * WirePacketsTests ForgeAnnounce cases.
 */
class ForgeAnnounceServiceTest {

    // ─── Byte-identity gate (fixtures/forge/vectors.json) ─────
    // Field order package_id, content_hash, size_bytes, announced_at_ms. size_bytes +
    // announced_at_ms as bare integers, no whitespace. Byte-identical with C# in every port.

    @Test fun forgeAnnounce_serializesToCanonicalBytes() {
        val json = ForgeAnnouncePayload(
            packageId = "npm:react@18.2.0",
            contentHash = "QmForgeHash456",
            sizeBytes = 294912,
            announcedAtMs = 1_700_000_000_000L,
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"package_id\":\"npm:react@18.2.0\",\"content_hash\":\"QmForgeHash456\",\"size_bytes\":294912,\"announced_at_ms\":1700000000000}",
            json,
        )
    }

    // ─── Broadcast + handle ─────────────────────────────────

    @Test fun forge_broadcast_emitsAnnouncePacket_andHandleRaisesEvent() = runBlocking {
        val sender = FakeMeshSender("aether:alice:01")
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:aa", identityKey = ByteArray(0)))
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:bb", identityKey = ByteArray(0)))
        val svc = ForgeAnnounceService(sender)

        val reached = svc.broadcast("npm:react@18.2.0", "QmForgeHash456", 294912, 1_700_000_000_000L)
        assertEquals(2, reached)
        assertEquals(1, sender.broadcasts.size)
        val sent = sender.broadcasts[0]
        assertEquals(PacketType.ForgeAnnounce, sent.type)

        var got: ForgeAnnouncePayload? = null
        svc.onAnnounceReceived = { got = it }
        assertTrue(svc.handle(sent))
        assertNotNull(got)
        assertEquals("npm:react@18.2.0", got!!.packageId)
        assertEquals(294912, got!!.sizeBytes)
    }

    @Test fun forge_handle_wrongType_returnsFalse() = runBlocking {
        val svc = ForgeAnnounceService(FakeMeshSender("aether:local:01"))
        assertFalse(svc.handle(MeshPacket(type = PacketType.Data, payload = ByteArray(0))))
    }
}
