// SPDX-License-Identifier: MIT
package aethernet.space

import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for the SpaceBreadcrumb wire binding ([PacketType.SpaceBreadcrumb], 40).
 * Byte-identity gates (fixtures/space/vectors.json) + broadcast/handle behaviour.
 * Uses the in-memory [FakeMeshSender] — no transport needed. Mirrors the C#
 * WirePacketsTests SpaceBreadcrumb cases.
 */
class SpaceBreadcrumbServiceTest {

    // ─── Byte-identity gate (fixtures/space/vectors.json) ─────
    // Field order content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature.
    // created_at_ms + ttl_hours + type as bare integers, signature STANDARD base64 (empty -> ""),
    // no whitespace. Must be byte-identical with C# in every language port.

    @Test fun spaceBreadcrumb_emergencySigned_serializesToCanonicalBytes() {
        val json = SpaceBreadcrumbCodec.toJsonBytes(
            SpaceBreadcrumb(
                contentHash = "QmContentHashExample123",
                geoHash = "u4pruy",
                anchorUhid = "aether:alice:01",
                createdAtUtc = Instant.ofEpochMilli(1_700_000_000_000L),
                ttlHours = 720,
                type = BreadcrumbType.EMERGENCY,
                signature = ByteArray(64) { 0x99.toByte() },
            )
        ).toString(Charsets.UTF_8)
        assertEquals(
            "{\"content_hash\":\"QmContentHashExample123\",\"geo_hash\":\"u4pruy\",\"anchor_uhid\":\"aether:alice:01\"," +
                "\"created_at_ms\":1700000000000,\"ttl_hours\":720,\"type\":1," +
                "\"signature\":\"mZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmQ==\"}",
            json,
        )
    }

    @Test fun spaceBreadcrumb_noticeUnsigned_serializesToCanonicalBytes() {
        val json = SpaceBreadcrumbCodec.toJsonBytes(
            SpaceBreadcrumb(
                contentHash = "QmNotice777",
                geoHash = "gcpvj0",
                anchorUhid = "aether:bob:02",
                createdAtUtc = Instant.ofEpochMilli(0L),
                ttlHours = 72,
                type = BreadcrumbType.NOTICE,
                signature = ByteArray(0),
            )
        ).toString(Charsets.UTF_8)
        assertEquals(
            "{\"content_hash\":\"QmNotice777\",\"geo_hash\":\"gcpvj0\",\"anchor_uhid\":\"aether:bob:02\"," +
                "\"created_at_ms\":0,\"ttl_hours\":72,\"type\":0,\"signature\":\"\"}",
            json,
        )
    }

    // ─── Broadcast + handle ─────────────────────────────────

    @Test fun space_broadcast_emitsBreadcrumbPacket_andHandleRaisesEvent() = runBlocking {
        val sender = FakeMeshSender("aether:alice:01")
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:aa", identityKey = ByteArray(0)))
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:bb", identityKey = ByteArray(0)))
        val svc = SpaceBreadcrumbService(sender)

        val crumb = SpaceBreadcrumb(
            contentHash = "QmX",
            geoHash = "u4pruy",
            anchorUhid = "aether:alice:01",
            createdAtUtc = Instant.ofEpochMilli(1_700_000_000_000L),
            ttlHours = 720,
            type = BreadcrumbType.EMERGENCY,
            signature = ByteArray(64) { 0x99.toByte() },
        )
        val reached = svc.broadcast(crumb)
        assertEquals(2, reached)
        assertEquals(1, sender.broadcasts.size)
        val sent = sender.broadcasts[0]
        assertEquals(PacketType.SpaceBreadcrumb, sent.type)

        var got: SpaceBreadcrumb? = null
        svc.onBreadcrumbReceived = { got = it }
        assertTrue(svc.handle(sent))
        assertNotNull(got)
        assertEquals("u4pruy", got!!.geoHash)
        assertEquals(BreadcrumbType.EMERGENCY, got!!.type)
        assertEquals(720, got!!.ttlHours)
        assertEquals(64, got!!.signature.size)
    }

    @Test fun space_handle_wrongType_returnsFalse() = runBlocking {
        val svc = SpaceBreadcrumbService(FakeMeshSender("aether:local:01"))
        assertFalse(svc.handle(MeshPacket(type = PacketType.Data, payload = ByteArray(0))))
    }
}
