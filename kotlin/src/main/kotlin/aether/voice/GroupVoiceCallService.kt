// SPDX-License-Identifier: MIT

package aether.voice

import aether.AetherConstants
import aether.protocol.MeshPacket
import aether.protocol.PacketType
import aether.routing.MeshSender
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

enum class GroupVoiceCallState { Invited, Active, Ended }

data class GroupVoiceCallSession(
    val id: UUID,
    val hostUhid: String,
    val members: MutableSet<String> = ConcurrentHashMap.newKeySet(),
    var state: GroupVoiceCallState = GroupVoiceCallState.Invited,
    /** Current key-generation counter. Host increments on each join/leave. */
    @Volatile var keyGeneration: Int = 0
) {
    val keyGenerationUInt: UInt get() = keyGeneration.toUInt()
}

/**
 * Group voice call service (up to [AetherConstants.MAX_GROUP_VOICE_MEMBERS] members).
 *
 * Wire format — GroupVoiceFrame binary payload:
 *   [16] CallId      (UUID RFC4122 big-endian)
 *   [4]  Sequence    (UInt32 little-endian)
 *   [8]  TimestampMs (Int64 little-endian)
 *   [1]  IsSilence   (0 or 1)
 *   [4]  KeyGeneration (UInt32 little-endian)
 *   [N]  EncodedPayload
 *
 * Signaling uses GroupVoiceSignalingMessage JSON (snake_case).
 *
 * Key rotation: when a member joins or leaves the host increments keyGeneration
 * and broadcasts a "key_rotation" signaling packet so all members re-key.
 *
 * Priority: 64 for frames, 32 for signaling.
 */
class GroupVoiceCallService(private val sender: MeshSender) {

    private val sessions = ConcurrentHashMap<UUID, GroupVoiceCallSession>()
    @Volatile private var frameSequence: Int = 0

    /** Fired when this node is invited to a group call. */
    var onInvited: ((GroupVoiceCallSession) -> Unit)? = null

    /** Fired when a member joins, leaves, is kicked, or the call ends. */
    var onMembershipChanged: ((GroupVoiceCallSession) -> Unit)? = null

    /** Fired when a new key generation is announced (re-keying needed). */
    var onKeyRotation: ((UUID, UInt) -> Unit)? = null

    /** Fired when a group voice frame arrives. */
    var onFrameReceived: ((UUID, String, ByteArray, Boolean, UInt) -> Unit)? = null

    // ─────────────────────────────────────────────────────────────────────
    // Host API
    // ─────────────────────────────────────────────────────────────────────

    /**
     * (Host) Invite [memberUhids] into an existing or new call [callId].
     */
    suspend fun invite(callId: UUID, memberUhids: List<String>) {
        require(memberUhids.isNotEmpty()) { "memberUhids must not be empty" }
        val session = sessions.getOrPut(callId) {
            GroupVoiceCallSession(
                id = callId,
                hostUhid = sender.localUhid,
                state = GroupVoiceCallState.Active
            ).also { it.members.add(sender.localUhid) }
        }

        for (uhid in memberUhids) {
            session.members.add(uhid)
            val payload = encodeGroupSignaling(
                kind = "invite",
                callId = callId,
                fromUhid = sender.localUhid,
                toUhid = uhid,
                invitedUhids = memberUhids,
                keyGeneration = session.keyGenerationUInt
            )
            sender.send(groupSignalingPacket(uhid, payload), uhid)
        }
    }

    /**
     * Join the call [callId] (called by an invited member).
     * The host will receive the join notification and rotate the key.
     */
    suspend fun join(callId: UUID) {
        val session = sessions[callId] ?: return
        session.members.add(sender.localUhid)
        session.state = GroupVoiceCallState.Active

        val payload = encodeGroupSignaling(
            kind = "join",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = session.hostUhid
        )
        sender.send(groupSignalingPacket(session.hostUhid, payload), session.hostUhid)

        // If local node is host, rotate key immediately
        if (sender.localUhid == session.hostUhid) {
            rotateKey(session)
        }
    }

    /**
     * Leave the call [callId].
     */
    suspend fun leave(callId: UUID) {
        val session = sessions[callId] ?: return
        session.members.remove(sender.localUhid)
        val isHost = sender.localUhid == session.hostUhid

        // Notify remaining members
        val leavePayload = encodeGroupSignaling(
            kind = "leave",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = ""
        )
        broadcastToMembers(session, leavePayload)

        if (isHost) {
            // Host leaving: end the call
            endCall(callId)
        } else if (session.members.contains(session.hostUhid)) {
            // Host is still present; ask host to rotate key by sending leave directly
            // The host's handlePacket will trigger rotation
        }

        if (session.members.isEmpty() || isHost) {
            sessions.remove(callId)
            session.state = GroupVoiceCallState.Ended
            onMembershipChanged?.invoke(session)
        }
    }

    /**
     * (Host) Kick [targetUhid] from the call.
     */
    suspend fun kick(callId: UUID, targetUhid: String) {
        val session = sessions[callId] ?: return
        if (sender.localUhid != session.hostUhid) return
        session.members.remove(targetUhid)

        val payload = encodeGroupSignaling(
            kind = "kick",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = targetUhid,
            kickedUhid = targetUhid
        )
        // Notify the kicked user
        sender.send(groupSignalingPacket(targetUhid, payload), targetUhid)
        // Rotate key so kicked member cannot decrypt future frames
        rotateKey(session)
        onMembershipChanged?.invoke(session)
    }

    /**
     * Send an encoded audio frame to all call members.
     * [keyGeneration] must match the current session key generation.
     */
    suspend fun sendFrame(callId: UUID, encodedAudio: ByteArray, isSilence: Boolean, keyGeneration: UInt) {
        val session = sessions[callId]
        if (session == null || session.state != GroupVoiceCallState.Active) return

        val seq = frameSequence++.toUInt()
        val payload = encodeGroupVoiceFrame(
            callId = callId,
            sequence = seq,
            timestampMs = System.currentTimeMillis(),
            isSilence = isSilence,
            keyGeneration = keyGeneration,
            encodedPayload = encodedAudio
        )
        val packet = MeshPacket(
            type = PacketType.VoiceCall,
            sourceUhid = sender.localUhid,
            destinationUhid = "",
            ttl = AetherConstants.DEFAULT_TTL,
            priority = 64,
            payload = payload
        )
        // Send to each member except self
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
            PacketType.VoiceSignaling -> handleSignaling(packet)
            PacketType.VoiceCall -> handleFrame(packet)
            else -> {}
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────

    private suspend fun handleSignaling(packet: MeshPacket) {
        val json = String(packet.payload, Charsets.UTF_8)
        val kind = JsonReader.readString(json, "kind") ?: return
        val callIdStr = JsonReader.readString(json, "call_id") ?: return
        val callId = runCatching { UUID.fromString(callIdStr) }.getOrNull() ?: return
        val fromUhid = JsonReader.readString(json, "from_uhid") ?: return

        when (kind) {
            "invite" -> {
                val invitedUhids = JsonReader.readStringArray(json, "invited_uhids")
                val keyGen = (JsonReader.readLong(json, "key_generation") ?: 0L).toInt()
                val session = GroupVoiceCallSession(
                    id = callId,
                    hostUhid = fromUhid,
                    state = GroupVoiceCallState.Invited,
                    keyGeneration = keyGen
                )
                session.members.addAll(invitedUhids)
                session.members.add(fromUhid)
                sessions[callId] = session
                onInvited?.invoke(session)
            }
            "join" -> {
                val session = sessions[callId] ?: return
                session.members.add(fromUhid)
                // If we are the host, rotate key on member join
                if (sender.localUhid == session.hostUhid) {
                    rotateKey(session)
                }
                onMembershipChanged?.invoke(session)
            }
            "leave" -> {
                val session = sessions[callId] ?: return
                session.members.remove(fromUhid)
                if (sender.localUhid == session.hostUhid) {
                    rotateKey(session)
                }
                onMembershipChanged?.invoke(session)
            }
            "kick" -> {
                val session = sessions[callId] ?: return
                val kickedUhid = JsonReader.readString(json, "kicked_uhid")
                if (kickedUhid != null) session.members.remove(kickedUhid)
                if (sender.localUhid == kickedUhid) {
                    sessions.remove(callId)
                    session.state = GroupVoiceCallState.Ended
                }
                onMembershipChanged?.invoke(session)
            }
            "end" -> {
                val session = sessions.remove(callId) ?: return
                session.state = GroupVoiceCallState.Ended
                onMembershipChanged?.invoke(session)
            }
            "key_rotation" -> {
                val session = sessions[callId] ?: return
                val keyGen = (JsonReader.readLong(json, "key_generation") ?: 0L).toInt()
                session.keyGeneration = keyGen
                onKeyRotation?.invoke(callId, session.keyGenerationUInt)
            }
        }
    }

    private fun handleFrame(packet: MeshPacket) {
        val data = packet.payload
        // Minimum: 16 (CallId) + 4 (seq) + 8 (ts) + 1 (silence) + 4 (keyGen) = 33 bytes
        if (data.size < 33) return

        val uuidBuf = ByteBuffer.wrap(data, 0, 16).order(ByteOrder.BIG_ENDIAN)
        val callId = UUID(uuidBuf.long, uuidBuf.long)

        val buf = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)
        buf.position(16)
        @Suppress("UNUSED_VARIABLE") val seq = buf.int.toUInt()
        @Suppress("UNUSED_VARIABLE") val ts = buf.long
        val isSilence = buf.get() != 0.toByte()
        val keyGen = buf.int.toUInt()
        val encoded = if (buf.hasRemaining()) {
            val arr = ByteArray(buf.remaining())
            buf.get(arr)
            arr
        } else ByteArray(0)

        onFrameReceived?.invoke(callId, packet.sourceUhid, encoded, isSilence, keyGen)
    }

    private suspend fun rotateKey(session: GroupVoiceCallSession) {
        session.keyGeneration++
        val payload = encodeGroupSignaling(
            kind = "key_rotation",
            callId = session.id,
            fromUhid = sender.localUhid,
            toUhid = "",
            keyGeneration = session.keyGenerationUInt
        )
        broadcastToMembers(session, payload)
        onKeyRotation?.invoke(session.id, session.keyGenerationUInt)
    }

    private suspend fun endCall(callId: UUID) {
        val session = sessions.remove(callId) ?: return
        session.state = GroupVoiceCallState.Ended
        val payload = encodeGroupSignaling(
            kind = "end",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = ""
        )
        broadcastToMembers(session, payload)
        onMembershipChanged?.invoke(session)
    }

    private suspend fun broadcastToMembers(session: GroupVoiceCallSession, payload: ByteArray) {
        for (member in session.members) {
            if (member != sender.localUhid) {
                val pkt = groupSignalingPacket(member, payload)
                sender.send(pkt, member)
            }
        }
    }

    private fun groupSignalingPacket(toUhid: String, payload: ByteArray) = MeshPacket(
        type = PacketType.VoiceSignaling,
        sourceUhid = sender.localUhid,
        destinationUhid = toUhid,
        ttl = AetherConstants.DEFAULT_TTL,
        priority = 32,
        payload = payload
    )
}

// ─────────────────────────────────────────────────────────────────────────────
// Wire helpers
// ─────────────────────────────────────────────────────────────────────────────

internal fun encodeGroupVoiceFrame(
    callId: UUID,
    sequence: UInt,
    timestampMs: Long,
    isSilence: Boolean,
    keyGeneration: UInt,
    encodedPayload: ByteArray
): ByteArray {
    val buf = ByteBuffer.allocate(16 + 4 + 8 + 1 + 4 + encodedPayload.size)
    buf.order(ByteOrder.BIG_ENDIAN)
    buf.putLong(callId.mostSignificantBits)
    buf.putLong(callId.leastSignificantBits)
    buf.order(ByteOrder.LITTLE_ENDIAN)
    buf.putInt(sequence.toInt())
    buf.putLong(timestampMs)
    buf.put(if (isSilence) 1.toByte() else 0.toByte())
    buf.putInt(keyGeneration.toInt())
    buf.put(encodedPayload)
    return buf.array()
}

private fun encodeGroupSignaling(
    kind: String,
    callId: UUID,
    fromUhid: String,
    toUhid: String,
    invitedUhids: List<String>? = null,
    kickedUhid: String? = null,
    keyGeneration: UInt? = null
): ByteArray {
    val sb = StringBuilder()
    sb.append('{')
    sb.append("\"kind\":\"").append(jsonEscape(kind)).append("\",")
    sb.append("\"call_id\":\"").append(callId).append("\",")
    sb.append("\"from_uhid\":\"").append(jsonEscape(fromUhid)).append("\",")
    sb.append("\"to_uhid\":\"").append(jsonEscape(toUhid)).append('"')
    if (invitedUhids != null) {
        sb.append(",\"invited_uhids\":[")
        invitedUhids.forEachIndexed { i, u ->
            if (i > 0) sb.append(',')
            sb.append('"').append(jsonEscape(u)).append('"')
        }
        sb.append(']')
    }
    if (kickedUhid != null) sb.append(",\"kicked_uhid\":\"").append(jsonEscape(kickedUhid)).append('"')
    if (keyGeneration != null) sb.append(",\"key_generation\":").append(keyGeneration.toLong())
    sb.append('}')
    return sb.toString().toByteArray(Charsets.UTF_8)
}
