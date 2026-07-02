// SPDX-License-Identifier: MIT
package aethernet.profiles

import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Unit tests for [ProfileService] (PacketType.ProfileSync). Directed exchange — the in-memory
 * [FakeMeshSender] captures the directed send. Mirrors the C# ProfileSyncTests.
 */
private const val LOCAL = "aether:local:01"

private data class PrSvc(val svc: ProfileService, val sender: FakeMeshSender)

private fun newSvc(localUhid: String = LOCAL): PrSvc {
    val sender = FakeMeshSender(localUhid)
    return PrSvc(ProfileService(sender), sender)
}

private fun profilePacket(
    uhid: String,
    name: String,
    avatar: String,
    status: String,
    updatedAtMs: Long
): MeshPacket = MeshPacket(
    type = PacketType.ProfileSync,
    sourceUhid = uhid,
    destinationUhid = LOCAL,
    payload = ProfileSyncPayload(
        uhid = uhid,
        displayName = name,
        avatarRef = avatar,
        statusMessage = status,
        updatedAtMs = updatedAtMs
    ).toJsonBytes(),
)

class ProfileSyncServiceTest {

    // ─── Byte-identity gate (fixtures/profiles/vectors.json) ─────
    // snake_case, field order uhid, display_name, avatar_ref, status_message, updated_at_ms, no
    // whitespace, all string fields always present, updated_at_ms a bare integer. Byte-identical with C#.

    @Test fun profileSyncPayload_serializesToCanonicalBytes_vector1() {
        val json = ProfileSyncPayload(
            uhid = "aether:alice:01",
            displayName = "Alice",
            avatarRef = "blake3:abc",
            statusMessage = "available",
            updatedAtMs = 1_700_000_000_000L
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"uhid\":\"aether:alice:01\",\"display_name\":\"Alice\",\"avatar_ref\":\"blake3:abc\",\"status_message\":\"available\",\"updated_at_ms\":1700000000000}",
            json
        )
    }

    @Test fun profileSyncPayload_serializesToCanonicalBytes_vector2() {
        val json = ProfileSyncPayload(
            uhid = "n",
            displayName = "",
            avatarRef = "",
            statusMessage = "",
            updatedAtMs = 0L
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"uhid\":\"n\",\"display_name\":\"\",\"avatar_ref\":\"\",\"status_message\":\"\",\"updated_at_ms\":0}",
            json
        )
    }

    // ─── PublishProfileTo ───────────────────────────────────

    @Test fun publishProfileTo_sendsDirectedProfileToPeer() = runBlocking {
        val (svc, sender) = newSvc("aether:alice:01")
        svc.setLocalProfile("Alice", "blake3:abc", "available")

        val ok = svc.publishProfileTo("aether:bob:02")

        assertTrue(ok)
        assertEquals(1, sender.unicasts.size)
        val sent = sender.unicasts[0]
        assertEquals(PacketType.ProfileSync, sent.packet.type)
        assertEquals("aether:bob:02", sent.nextHopUhid)
        assertEquals("aether:bob:02", sent.packet.destinationUhid)
        val body = sent.packet.payload.toString(Charsets.UTF_8)
        assertTrue(body.contains("\"uhid\":\"aether:alice:01\""), body)
        assertTrue(body.contains("\"display_name\":\"Alice\""), body)
    }

    @Test fun publishProfileTo_returnsFalseWhenPeerUnreachable() = runBlocking {
        val sender = FakeMeshSender("aether:alice:01")
        sender.failSendsTo("aether:bob:02")
        val svc = ProfileService(sender)
        svc.setLocalProfile("Alice", "", "")

        assertFalse(svc.publishProfileTo("aether:bob:02"))
    }

    @Test fun setLocalProfile_stampsUhidAndUpdatedAt() {
        val (svc, _) = newSvc("aether:alice:01")
        svc.setLocalProfile("Alice", "blake3:abc", "available")

        val local = svc.getLocalProfile()
        assertEquals("aether:alice:01", local.uhid)
        assertEquals("Alice", local.displayName)
        assertEquals("blake3:abc", local.avatarRef)
        assertEquals("available", local.statusMessage)
        assertTrue(local.updatedAtMs > 0L)
    }

    // ─── Handle ─────────────────────────────────────────────

    @Test fun handle_cachesPeerProfileAndRaisesEvent() = runBlocking {
        val (svc, _) = newSvc()
        var updated: ProfileSyncPayload? = null
        svc.onProfileUpdated = { updated = it }

        val ok = svc.handle(profilePacket("aether:bob:02", "Bob", "blake3:xyz", "busy", 1_700_000_000_000L))

        assertTrue(ok)
        assertNotNull(updated)
        assertEquals("Bob", updated!!.displayName)

        val cached = svc.getProfile("aether:bob:02")
        assertNotNull(cached)
        assertEquals("busy", cached!!.statusMessage)
        assertEquals("blake3:xyz", cached.avatarRef)
        assertEquals(1_700_000_000_000L, cached.updatedAtMs)
        assertEquals(1, svc.getKnownProfiles().size)
    }

    @Test fun handle_refreshesExistingProfile() = runBlocking {
        val (svc, _) = newSvc()
        svc.handle(profilePacket("aether:bob:02", "Bob", "", "here", 1000L))
        svc.handle(profilePacket("aether:bob:02", "Bob", "", "away", 2000L))

        val cached = svc.getProfile("aether:bob:02")
        assertEquals("away", cached!!.statusMessage)
        assertEquals(2000L, cached.updatedAtMs)
        assertEquals(1, svc.getKnownProfiles().size)
    }

    @Test fun handle_ownProfile_isIgnored() = runBlocking {
        val (svc, _) = newSvc(LOCAL)
        val ok = svc.handle(profilePacket(LOCAL, "Me", "", "", 1L))
        assertFalse(ok)
        assertTrue(svc.getKnownProfiles().isEmpty())
    }

    @Test fun handle_wrongPacketType_returnsFalse() = runBlocking {
        val (svc, _) = newSvc()
        val pkt = profilePacket("aether:bob:02", "Bob", "", "", 1L).apply { type = PacketType.Data }
        assertFalse(svc.handle(pkt))
        assertTrue(svc.getKnownProfiles().isEmpty())
    }

    @Test fun handle_malformedPayload_returnsFalse() = runBlocking {
        val (svc, _) = newSvc()
        val pkt = MeshPacket(
            type = PacketType.ProfileSync,
            sourceUhid = "aether:bob:02",
            destinationUhid = LOCAL,
            payload = "not json".toByteArray(Charsets.UTF_8),
        )
        assertFalse(svc.handle(pkt))
        assertTrue(svc.getKnownProfiles().isEmpty())
    }

    @Test fun getProfile_unknownUhid_returnsNull() {
        val (svc, _) = newSvc()
        assertNull(svc.getProfile("aether:nobody:00"))
    }
}
