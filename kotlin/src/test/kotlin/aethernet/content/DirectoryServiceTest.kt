// SPDX-License-Identifier: MIT
package aethernet.content

import aethernet.FakeMeshSender
import aethernet.models.NodeCapabilities
import aethernet.models.PeerInfo
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Tests for [DirectoryService] — application-layer name resolution added in
 * v1.2.0 (Issue #60). Mirrors the C# DirectoryServiceTests.cs suite.
 */
class DirectoryServiceTest {

    private fun sampleDescriptor(rootHash: String = "deadbeef") = ContentDescriptor(
        rootHash = rootHash,
        name = "ignored-publisher-hint",
        totalBytes = 1024L,
        chunkSizeBytes = 256,
        chunkCount = 4,
        chunkHashes = listOf("h0", "h1", "h2", "h3"),
        contentType = "audio/flac",
    )

    private fun peer(uhid: String) = PeerInfo(
        uhid = uhid,
        identityKey = ByteArray(0),
        capabilities = NodeCapabilities(),
    )

    // ─── publish ─────────────────────────────────────────────────────────

    @Test fun publish_storesLocallyAndBroadcastsNamePublish() = runBlocking {
        val sender = FakeMeshSender("publisher")
        sender.addPeer(peer("peer-1"))
        sender.addPeer(peer("peer-2"))
        val dir = DefaultDirectoryService(sender)

        dir.publish("podcast:abc", sampleDescriptor("root-abc"))

        // Local resolve hits the catalogue immediately (no broadcast).
        val before = sender.broadcasts.size
        val hit = dir.resolve("podcast:abc", timeoutMs = 50L)
        assertNotNull(hit)
        assertEquals("root-abc", hit!!.rootHash)
        // resolve added zero broadcasts because of local hit
        assertEquals(before, sender.broadcasts.size)

        // The publish broadcast itself went out.
        assertEquals(1, sender.broadcasts.size)
        assertEquals(PacketType.NamePublish, sender.broadcasts.first().type)
    }

    @Test fun resolve_localCatalogueHit_returnsImmediately_noQueryBroadcast() = runBlocking {
        val sender = FakeMeshSender("local")
        sender.addPeer(peer("peer-1"))
        val dir = DefaultDirectoryService(sender)

        dir.publish("track:xyz", sampleDescriptor("root-xyz"))
        sender.clear()

        val hit = dir.resolve("track:xyz")

        assertNotNull(hit)
        assertEquals("root-xyz", hit!!.rootHash)
        assertEquals(0, sender.broadcasts.size) // no NameQuery sent — local hit
    }

    // ─── handle(NamePublish) — inbound ────────────────────────────────────

    @Test fun handle_inboundNamePublish_populatesCatalogueAndFiresEvent() = runBlocking {
        val sender = FakeMeshSender("local")
        val dir = DefaultDirectoryService(sender)

        var captured: DirectoryEntryAnnouncedEvent? = null
        dir.onEntryAnnounced = { captured = it }

        // Build a NamePublish packet as if from a remote publisher.
        val descriptor = sampleDescriptor("from-peer")
        val publishBody = NamePublishPayload(
            name = "reel:hello",
            descriptor = descriptor,
            inResponseToQueryId = null,
        )
        val packet = MeshPacket(
            type = PacketType.NamePublish,
            sourceUhid = "peer-publisher",
            destinationUhid = "",
            payload = publishBody.toJson().toByteArray(Charsets.UTF_8),
        )
        dir.handle(packet)

        // Local catalogue now has the entry.
        val hit = dir.resolve("reel:hello", timeoutMs = 50L)
        assertNotNull(hit)
        assertEquals("from-peer", hit!!.rootHash)

        // Event fired.
        assertNotNull(captured)
        assertEquals("reel:hello", captured!!.name)
        assertEquals("peer-publisher", captured!!.sourceUhid)
        assertEquals("from-peer", captured!!.descriptor.rootHash)
    }

    // ─── handle(NameQuery) — answer if held ──────────────────────────────

    @Test fun handle_queryForHeldName_unicastsNamePublishResponse() = runBlocking {
        val sender = FakeMeshSender("holder")
        sender.addPeer(peer("asker"))
        val dir = DefaultDirectoryService(sender)

        dir.publish("album:test", sampleDescriptor("album-root"))
        sender.clear()

        val queryId = UUID.randomUUID().toString()
        val query = NameQueryPayload(name = "album:test", queryId = queryId)
        val queryPacket = MeshPacket(
            type = PacketType.NameQuery,
            sourceUhid = "asker",
            destinationUhid = "",
            payload = query.toJson().toByteArray(Charsets.UTF_8),
        )

        dir.handle(queryPacket)

        // Holder unicasts back a NamePublish with inResponseToQueryId set.
        assertEquals(1, sender.unicasts.size)
        val (responsePacket, nextHop) = sender.unicasts.first()
        assertEquals("asker", nextHop)
        assertEquals(PacketType.NamePublish, responsePacket.type)

        val responseBody = NamePublishPayload.fromJson(
            String(responsePacket.payload, Charsets.UTF_8)
        )!!
        assertEquals("album:test", responseBody.name)
        assertEquals("album-root", responseBody.descriptor.rootHash)
        assertEquals(queryId, responseBody.inResponseToQueryId)
    }

    @Test fun handle_queryForUnknownName_doesNothing() = runBlocking {
        val sender = FakeMeshSender("local")
        sender.addPeer(peer("asker"))
        val dir = DefaultDirectoryService(sender)

        val query = NameQueryPayload(name = "nothing-here", queryId = UUID.randomUUID().toString())
        val packet = MeshPacket(
            type = PacketType.NameQuery,
            sourceUhid = "asker",
            destinationUhid = "",
            payload = query.toJson().toByteArray(Charsets.UTF_8),
        )

        dir.handle(packet)

        assertEquals(0, sender.unicasts.size)
        assertEquals(0, sender.broadcasts.size)
    }

    // ─── resolve — timeout / waiting ─────────────────────────────────────

    @Test fun resolve_missAndTimeout_returnsNull() = runBlocking {
        val sender = FakeMeshSender("local")
        sender.addPeer(peer("peer-1"))
        val dir = DefaultDirectoryService(sender)

        val hit = dir.resolve("unknown-name", timeoutMs = 100L)

        assertNull(hit)
        // A NameQuery WAS broadcast — we tried.
        assertEquals(1, sender.broadcasts.size)
        assertEquals(PacketType.NameQuery, sender.broadcasts.first().type)
    }

    @Test fun resolve_queryAndAnswerArrives_returnsDescriptor() = runBlocking {
        val sender = FakeMeshSender("local")
        sender.addPeer(peer("peer-1"))
        val dir = DefaultDirectoryService(sender)

        // Start a resolve in the background; give it a generous timeout so
        // the test isn't time-sensitive on slow CI.
        val resolveTask = async { dir.resolve("podcast:remote", timeoutMs = 5_000L) }

        // Wait briefly for the NameQuery to be broadcast.
        var attempt = 0
        while (sender.broadcasts.isEmpty() && attempt < 20) {
            delay(25)
            attempt++
        }
        assertEquals(1, sender.broadcasts.size)
        val queryBroadcast = sender.broadcasts.first()
        assertEquals(PacketType.NameQuery, queryBroadcast.type)
        val query = NameQueryPayload.fromJson(
            String(queryBroadcast.payload, Charsets.UTF_8)
        )!!

        // Simulate a peer responding with a NamePublish carrying inResponseToQueryId.
        val descriptor = sampleDescriptor("remote-root")
        val responseBody = NamePublishPayload(
            name = "podcast:remote",
            descriptor = descriptor,
            inResponseToQueryId = query.queryId,
        )
        val responsePacket = MeshPacket(
            type = PacketType.NamePublish,
            sourceUhid = "peer-1",
            destinationUhid = "local",
            payload = responseBody.toJson().toByteArray(Charsets.UTF_8),
        )
        dir.handle(responsePacket)

        val result = resolveTask.await()
        assertNotNull(result)
        assertEquals("remote-root", result!!.rootHash)
    }

    // ─── listNames ───────────────────────────────────────────────────────

    @Test fun listNames_returnsCatalogueSnapshot() = runBlocking {
        val sender = FakeMeshSender("local")
        val dir = DefaultDirectoryService(sender)

        dir.publish("a", sampleDescriptor("hash-a"))
        dir.publish("b", sampleDescriptor("hash-b"))
        dir.publish("c", sampleDescriptor("hash-c"))

        val names = dir.listNames()

        assertEquals(3, names.size)
        assertTrue(names.contains("a"))
        assertTrue(names.contains("b"))
        assertTrue(names.contains("c"))
    }
}
