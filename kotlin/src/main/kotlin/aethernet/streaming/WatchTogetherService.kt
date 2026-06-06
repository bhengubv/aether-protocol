// SPDX-License-Identifier: MIT

package aethernet.streaming

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import aethernet.voice.jsonEscape
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

enum class WatchTogetherKind { Play, Pause, Seek, Speed, Join, Leave, End }

data class WatchSyncEvent(
    val sessionId: UUID,
    val kind: WatchTogetherKind,
    val positionMs: Long?,
    val playbackSpeed: Double?,
    val sentAtMs: Long,
    val contentId: String?,
    val fromUhid: String
) {
    /**
     * RTT-compensated playback position.
     * position = positionMs + (now - sentAtMs) * playbackSpeed
     */
    fun compensatedPositionMs(speed: Double = playbackSpeed ?: 1.0): Long? {
        val pos = positionMs ?: return null
        val latencyMs = System.currentTimeMillis() - sentAtMs
        return pos + (latencyMs * speed).toLong()
    }
}

data class WatchReactionEvent(
    val sessionId: UUID,
    val reaction: String,
    val fromUhid: String
)

data class WatchSession(
    val id: UUID,
    val contentId: String,
    val members: MutableSet<String> = ConcurrentHashMap.newKeySet()
)

/**
 * Synchronized watch-together service.
 *
 * Sync packets use WatchSyncPayload JSON, reaction packets use WatchReactionPayload JSON.
 * RTT compensation is applied on receipt: compensated_position = positionMs + Δt * speed.
 */
class WatchTogetherService(private val sender: MeshSender) {

    private val sessions = ConcurrentHashMap<UUID, WatchSession>()

    /** Fired when a sync event (play, pause, seek, speed, join, leave, end) arrives. */
    var onSyncReceived: ((WatchSyncEvent) -> Unit)? = null

    /** Fired when a reaction arrives. */
    var onReactionReceived: ((WatchReactionEvent) -> Unit)? = null

    // ─────────────────────────────────────────────────────────────────────
    // Session management
    // ─────────────────────────────────────────────────────────────────────

    /**
     * Create or join a session locally (no network message). Call [inviteToSession]
     * to send invites to remote members.
     */
    fun createSession(sessionId: UUID, contentId: String): WatchSession {
        return sessions.getOrPut(sessionId) {
            WatchSession(id = sessionId, contentId = contentId).also {
                it.members.add(sender.localUhid)
            }
        }
    }

    /**
     * Send an invite+join signal to [memberUhids] for the given [sessionId].
     */
    suspend fun inviteToSession(sessionId: UUID, contentId: String, memberUhids: List<String>) {
        require(memberUhids.isNotEmpty()) { "memberUhids must not be empty" }
        val session = sessions.getOrPut(sessionId) {
            WatchSession(id = sessionId, contentId = contentId).also {
                it.members.add(sender.localUhid)
            }
        }
        for (uhid in memberUhids) {
            session.members.add(uhid)
            val payload = encodeWatchSync(
                sessionId = sessionId,
                kind = "join",
                positionMs = null,
                playbackSpeed = null,
                contentId = contentId
            )
            sender.send(watchSyncPacket(uhid, payload), uhid)
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Playback control — broadcasts to all session members
    // ─────────────────────────────────────────────────────────────────────

    suspend fun play(sessionId: UUID, positionMs: Long) {
        broadcastSync(sessionId, "play", positionMs = positionMs)
    }

    suspend fun pause(sessionId: UUID, positionMs: Long) {
        broadcastSync(sessionId, "pause", positionMs = positionMs)
    }

    suspend fun seek(sessionId: UUID, positionMs: Long) {
        broadcastSync(sessionId, "seek", positionMs = positionMs)
    }

    suspend fun setSpeed(sessionId: UUID, playbackSpeed: Double) {
        broadcastSync(sessionId, "speed", playbackSpeed = playbackSpeed)
    }

    // ─────────────────────────────────────────────────────────────────────
    // Reactions
    // ─────────────────────────────────────────────────────────────────────

    suspend fun sendReaction(sessionId: UUID, reaction: String) {
        require(reaction.isNotEmpty()) { "reaction must not be empty" }
        val session = sessions[sessionId] ?: return
        val payload = encodeWatchReaction(sessionId, reaction)
        val packet = MeshPacket(
            type = PacketType.WatchReaction,
            sourceUhid = sender.localUhid,
            destinationUhid = "",
            ttl = AetherNetConstants.DEFAULT_TTL,
            priority = 16,
            payload = payload
        )
        for (member in session.members) {
            if (member != sender.localUhid) {
                sender.send(packet.copy(destinationUhid = member), member)
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Inbound packet dispatcher
    // ─────────────────────────────────────────────────────────────────────

    suspend fun handlePacket(packet: MeshPacket) {
        when (packet.type) {
            PacketType.WatchSync -> handleSync(packet)
            PacketType.WatchReaction -> handleReaction(packet)
            else -> {}
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────

    private suspend fun broadcastSync(
        sessionId: UUID,
        kind: String,
        positionMs: Long? = null,
        playbackSpeed: Double? = null
    ) {
        val session = sessions[sessionId] ?: return
        val payload = encodeWatchSync(
            sessionId = sessionId,
            kind = kind,
            positionMs = positionMs,
            playbackSpeed = playbackSpeed
        )
        val packet = MeshPacket(
            type = PacketType.WatchSync,
            sourceUhid = sender.localUhid,
            destinationUhid = "",
            ttl = AetherNetConstants.DEFAULT_TTL,
            priority = 32,
            payload = payload
        )
        for (member in session.members) {
            if (member != sender.localUhid) {
                sender.send(packet.copy(destinationUhid = member), member)
            }
        }
    }

    private fun handleSync(packet: MeshPacket) {
        val json = String(packet.payload, Charsets.UTF_8)
        val sessionIdStr = JsonReader.readString(json, "session_id") ?: return
        val sessionId = runCatching { UUID.fromString(sessionIdStr) }.getOrNull() ?: return
        val kindStr = JsonReader.readString(json, "kind") ?: return
        val positionMs = JsonReader.readLong(json, "position_ms")
        val playbackSpeed = JsonReader.readDouble(json, "playback_speed")
        val sentAtMs = JsonReader.readLong(json, "sent_at_ms") ?: System.currentTimeMillis()
        val contentId = JsonReader.readString(json, "content_id")

        val kind = when (kindStr) {
            "play" -> WatchTogetherKind.Play
            "pause" -> WatchTogetherKind.Pause
            "seek" -> WatchTogetherKind.Seek
            "speed" -> WatchTogetherKind.Speed
            "join" -> WatchTogetherKind.Join
            "leave" -> WatchTogetherKind.Leave
            "end" -> WatchTogetherKind.End
            else -> return
        }

        // Maintain session membership on join/leave/end
        when (kind) {
            WatchTogetherKind.Join -> {
                val session = sessions.getOrPut(sessionId) {
                    WatchSession(id = sessionId, contentId = contentId ?: "")
                }
                session.members.add(packet.sourceUhid)
            }
            WatchTogetherKind.Leave -> {
                sessions[sessionId]?.members?.remove(packet.sourceUhid)
            }
            WatchTogetherKind.End -> {
                sessions.remove(sessionId)
            }
            else -> {}
        }

        val event = WatchSyncEvent(
            sessionId = sessionId,
            kind = kind,
            positionMs = positionMs,
            playbackSpeed = playbackSpeed,
            sentAtMs = sentAtMs,
            contentId = contentId,
            fromUhid = packet.sourceUhid
        )
        onSyncReceived?.invoke(event)
    }

    private fun handleReaction(packet: MeshPacket) {
        val json = String(packet.payload, Charsets.UTF_8)
        val sessionIdStr = JsonReader.readString(json, "session_id") ?: return
        val sessionId = runCatching { UUID.fromString(sessionIdStr) }.getOrNull() ?: return
        val reaction = JsonReader.readString(json, "reaction") ?: return

        onReactionReceived?.invoke(
            WatchReactionEvent(sessionId = sessionId, reaction = reaction, fromUhid = packet.sourceUhid)
        )
    }

    private fun watchSyncPacket(toUhid: String, payload: ByteArray) = MeshPacket(
        type = PacketType.WatchSync,
        sourceUhid = sender.localUhid,
        destinationUhid = toUhid,
        ttl = AetherNetConstants.DEFAULT_TTL,
        priority = 32,
        payload = payload
    )
}

// ─────────────────────────────────────────────────────────────────────────────
// Wire helpers
// ─────────────────────────────────────────────────────────────────────────────

private fun encodeWatchSync(
    sessionId: UUID,
    kind: String,
    positionMs: Long?,
    playbackSpeed: Double?,
    contentId: String? = null
): ByteArray {
    val sentAtMs = System.currentTimeMillis()
    val sb = StringBuilder()
    sb.append('{')
    sb.append("\"session_id\":\"").append(sessionId).append("\",")
    sb.append("\"kind\":\"").append(jsonEscape(kind)).append("\",")
    sb.append("\"sent_at_ms\":").append(sentAtMs)
    if (positionMs != null) sb.append(",\"position_ms\":").append(positionMs)
    if (playbackSpeed != null) sb.append(",\"playback_speed\":").append(playbackSpeed)
    if (contentId != null) sb.append(",\"content_id\":\"").append(jsonEscape(contentId)).append('"')
    sb.append('}')
    return sb.toString().toByteArray(Charsets.UTF_8)
}

private fun encodeWatchReaction(sessionId: UUID, reaction: String): ByteArray {
    val s = "{\"session_id\":\"$sessionId\",\"reaction\":\"${jsonEscape(reaction)}\"}"
    return s.toByteArray(Charsets.UTF_8)
}
