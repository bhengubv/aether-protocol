// SPDX-License-Identifier: MIT
package aether.streaming

import aether.FakeMeshSender
import aether.protocol.MeshPacket
import aether.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

// ── Helpers ───────────────────────────────────────────────────────────────────

private fun makeSvc(uhid: String = "alice"): Pair<FakeMeshSender, WatchTogetherService> {
    val sender = FakeMeshSender(uhid)
    return sender to WatchTogetherService(sender)
}

private fun watchSyncPacket(from: String, json: String): MeshPacket =
    MeshPacket(
        type = PacketType.WatchSync,
        sourceUhid = from,
        payload = json.toByteArray(Charsets.UTF_8)
    )

private fun watchReactionPacket(from: String, sessionId: UUID, reaction: String): MeshPacket =
    MeshPacket(
        type = PacketType.WatchReaction,
        sourceUhid = from,
        payload = """{"session_id":"$sessionId","reaction":"$reaction"}"""
            .toByteArray(Charsets.UTF_8)
    )

private fun playSyncPacket(from: String, sessionId: UUID, positionMs: Long): MeshPacket =
    watchSyncPacket(
        from,
        """{"session_id":"$sessionId","kind":"play","sent_at_ms":${System.currentTimeMillis()},"position_ms":$positionMs}"""
    )

private fun pauseSyncPacket(from: String, sessionId: UUID, positionMs: Long): MeshPacket =
    watchSyncPacket(
        from,
        """{"session_id":"$sessionId","kind":"pause","sent_at_ms":${System.currentTimeMillis()},"position_ms":$positionMs}"""
    )

private fun seekSyncPacket(from: String, sessionId: UUID, positionMs: Long): MeshPacket =
    watchSyncPacket(
        from,
        """{"session_id":"$sessionId","kind":"seek","sent_at_ms":${System.currentTimeMillis()},"position_ms":$positionMs}"""
    )

private fun speedSyncPacket(from: String, sessionId: UUID, speed: Double): MeshPacket =
    watchSyncPacket(
        from,
        """{"session_id":"$sessionId","kind":"speed","sent_at_ms":${System.currentTimeMillis()},"playback_speed":$speed}"""
    )

private fun joinSyncPacket(from: String, sessionId: UUID, contentId: String): MeshPacket =
    watchSyncPacket(
        from,
        """{"session_id":"$sessionId","kind":"join","sent_at_ms":${System.currentTimeMillis()},"content_id":"$contentId"}"""
    )

// ── WatchTogetherServiceTest ──────────────────────────────────────────────────

class WatchTogetherServiceTest {

    // ── createSession ─────────────────────────────────────────────────────────

    @Test fun createSession_returnsSessionWithLocalUhid() {
        val (_, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        val session = svc.createSession(sessionId, "movie:123")

        assertEquals(sessionId, session.id)
        assertEquals("movie:123", session.contentId)
        assertTrue(session.members.contains("alice"), "alice should be in session members")
    }

    // ── inviteToSession ───────────────────────────────────────────────────────

    @Test fun inviteToSession_sendsJoinSyncToEachMember() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        svc.inviteToSession(sessionId, "movie:123", listOf("bob", "carol"))

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        val toCarol = sender.unicasts.filter { it.nextHopUhid == "carol" }
        assertTrue(toBob.isNotEmpty(), "expected join sync unicast to bob")
        assertTrue(toCarol.isNotEmpty(), "expected join sync unicast to carol")

        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"join\""), "expected kind=join in payload")
        assertEquals(PacketType.WatchSync, toBob[0].packet.type)
    }

    @Test fun inviteToSession_emptyMembers_throws() {
        val (_, svc) = makeSvc()
        var threw = false
        try {
            runBlocking { svc.inviteToSession(UUID.randomUUID(), "movie:1", emptyList()) }
        } catch (_: IllegalArgumentException) {
            threw = true
        }
        assertTrue(threw, "expected IllegalArgumentException for empty memberUhids")
    }

    // ── play ──────────────────────────────────────────────────────────────────

    @Test fun play_broadcastsPlaySyncToMembers() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        svc.inviteToSession(sessionId, "movie:123", listOf("bob"))
        sender.clear()

        svc.play(sessionId, positionMs = 5000L)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected play sync unicast to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"play\""), "expected kind=play in payload")
        assertTrue(json.contains("5000"), "expected positionMs in payload")
        assertEquals(PacketType.WatchSync, toBob[0].packet.type)
    }

    // ── pause ─────────────────────────────────────────────────────────────────

    @Test fun pause_broadcastsPauseSyncToMembers() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        svc.inviteToSession(sessionId, "movie:123", listOf("bob"))
        sender.clear()

        svc.pause(sessionId, positionMs = 12000L)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected pause sync to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"pause\""), "expected kind=pause")
    }

    // ── seek ──────────────────────────────────────────────────────────────────

    @Test fun seek_broadcastsSeekSyncToMembers() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        svc.inviteToSession(sessionId, "movie:123", listOf("bob"))
        sender.clear()

        svc.seek(sessionId, positionMs = 60000L)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected seek sync to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"seek\""), "expected kind=seek")
        assertTrue(json.contains("60000"), "expected positionMs in payload")
    }

    // ── setSpeed ──────────────────────────────────────────────────────────────

    @Test fun setSpeed_broadcastsSpeedSyncToMembers() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        svc.inviteToSession(sessionId, "movie:123", listOf("bob"))
        sender.clear()

        svc.setSpeed(sessionId, playbackSpeed = 1.5)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected speed sync to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"speed\""), "expected kind=speed")
        assertTrue(json.contains("1.5"), "expected playbackSpeed in payload")
    }

    // ── sendReaction ──────────────────────────────────────────────────────────

    @Test fun sendReaction_unicastsToAllMembersExceptSelf() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        svc.inviteToSession(sessionId, "movie:123", listOf("bob", "carol"))
        sender.clear()

        svc.sendReaction(sessionId, "👍")

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        val toCarol = sender.unicasts.filter { it.nextHopUhid == "carol" }
        val toAlice = sender.unicasts.filter { it.nextHopUhid == "alice" }
        assertTrue(toBob.isNotEmpty(), "bob should receive reaction")
        assertTrue(toCarol.isNotEmpty(), "carol should receive reaction")
        assertEquals(0, toAlice.size, "alice (self) must not receive own reaction")

        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("👍"), "expected reaction emoji in payload")
        assertEquals(PacketType.WatchReaction, toBob[0].packet.type)
    }

    // ── handlePacket — play sync ──────────────────────────────────────────────

    @Test fun handlePacket_playSyncPacket_firesOnSyncReceivedWithPlayKind() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        var event: WatchSyncEvent? = null
        svc.onSyncReceived = { event = it }

        svc.handlePacket(playSyncPacket("bob", sessionId, 5000L))

        assertTrue(event != null, "onSyncReceived not fired for play packet")
        assertEquals(WatchTogetherKind.Play, event?.kind)
        assertEquals(5000L, event?.positionMs)
        assertEquals("bob", event?.fromUhid)
        assertEquals(sessionId, event?.sessionId)
    }

    @Test fun handlePacket_pauseSyncPacket_firesOnSyncReceivedWithPauseKind() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        var event: WatchSyncEvent? = null
        svc.onSyncReceived = { event = it }

        svc.handlePacket(pauseSyncPacket("bob", sessionId, 12000L))

        assertTrue(event != null, "onSyncReceived not fired for pause packet")
        assertEquals(WatchTogetherKind.Pause, event?.kind)
        assertEquals(12000L, event?.positionMs)
    }

    @Test fun handlePacket_seekSyncPacket_firesWithSeekKind() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        var event: WatchSyncEvent? = null
        svc.onSyncReceived = { event = it }

        svc.handlePacket(seekSyncPacket("bob", sessionId, 60000L))

        assertTrue(event != null, "onSyncReceived not fired for seek packet")
        assertEquals(WatchTogetherKind.Seek, event?.kind)
        assertEquals(60000L, event?.positionMs)
    }

    @Test fun handlePacket_speedSyncPacket_firesWithSpeedKind() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        var event: WatchSyncEvent? = null
        svc.onSyncReceived = { event = it }

        svc.handlePacket(speedSyncPacket("bob", sessionId, 1.5))

        assertTrue(event != null, "onSyncReceived not fired for speed packet")
        assertEquals(WatchTogetherKind.Speed, event?.kind)
        assertEquals(1.5, event?.playbackSpeed)
    }

    // ── handlePacket — join sync adds member ──────────────────────────────────

    @Test fun handlePacket_joinSync_addsMemberToSession() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        // Pre-create session so it can receive the join
        svc.createSession(sessionId, "movie:123")

        var event: WatchSyncEvent? = null
        svc.onSyncReceived = { event = it }

        svc.handlePacket(joinSyncPacket("carol", sessionId, "movie:123"))

        assertTrue(event != null, "onSyncReceived not fired for join packet")
        assertEquals(WatchTogetherKind.Join, event?.kind)
        assertEquals("carol", event?.fromUhid)
    }

    // ── handlePacket — reaction ───────────────────────────────────────────────

    @Test fun handlePacket_reactionPacket_firesOnReactionReceived() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val sessionId = UUID.randomUUID()

        var reactionEvent: WatchReactionEvent? = null
        svc.onReactionReceived = { reactionEvent = it }

        svc.handlePacket(watchReactionPacket("bob", sessionId, "❤️"))

        assertTrue(reactionEvent != null, "onReactionReceived not fired")
        assertEquals("❤️", reactionEvent?.reaction)
        assertEquals("bob", reactionEvent?.fromUhid)
        assertEquals(sessionId, reactionEvent?.sessionId)
    }
}
