// SPDX-License-Identifier: MIT

package aethermesh.voice

import aethermesh.AetherMeshConstants
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import aethermesh.routing.MeshSender
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

enum class VoiceCallState { Outgoing, Incoming, Connected, Ended, Failed }

data class VoiceCallSession(
    val id: UUID,
    val callerUhid: String,
    val calleeUhid: String,
    var state: VoiceCallState,
    val proposedCodecs: List<String> = emptyList(),
    var selectedCodec: String? = null,
    var sampleRateHz: Int = 16000
)

/**
 * One-to-one voice call service.
 *
 * Wire format — VoiceFrame binary payload:
 *   [16] CallId  (UUID RFC4122 big-endian)
 *   [4]  Sequence (UInt32 little-endian)
 *   [8]  TimestampMs (Int64 little-endian)
 *   [1]  IsSilence (0 or 1)
 *   [N]  EncodedPayload
 *
 * Signaling uses VoiceSignalingMessage JSON (snake_case).
 *
 * Priority: 64 for voice frames, 32 for signaling.
 */
class VoiceCallService(private val sender: MeshSender) {

    private val sessions = ConcurrentHashMap<UUID, VoiceCallSession>()
    @Volatile private var frameSequence: Int = 0

    /** Invoked when an incoming call offer arrives. */
    var onIncomingCall: ((VoiceCallSession) -> Unit)? = null

    /** Invoked when a call state changes (answered, hung up, failed). */
    var onCallStateChanged: ((VoiceCallSession) -> Unit)? = null

    /** Invoked when a voice frame arrives for an active call. */
    var onFrameReceived: ((UUID, ByteArray, Boolean) -> Unit)? = null

    // ─────────────────────────────────────────────────────────────────────
    // Outbound API
    // ─────────────────────────────────────────────────────────────────────

    /**
     * Initiate a call to [toUhid]. Returns the new call-id.
     */
    suspend fun sendOffer(toUhid: String, codecs: List<String>, sampleRateHz: Int): UUID {
        require(toUhid.isNotEmpty()) { "toUhid must not be empty" }
        val callId = UUID.randomUUID()
        val session = VoiceCallSession(
            id = callId,
            callerUhid = sender.localUhid,
            calleeUhid = toUhid,
            state = VoiceCallState.Outgoing,
            proposedCodecs = codecs,
            sampleRateHz = sampleRateHz
        )
        sessions[callId] = session

        val payload = encodeSignaling(
            kind = "offer",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = toUhid,
            proposedCodecs = codecs,
            sampleRateHz = sampleRateHz
        )
        sender.send(signalingPacket(toUhid, payload), toUhid)
        return callId
    }

    /**
     * Accept an incoming call. Sends an "answer" signaling message.
     */
    suspend fun acceptCall(callId: UUID) {
        val session = sessions[callId] ?: return
        if (session.state != VoiceCallState.Incoming) return
        session.state = VoiceCallState.Connected
        onCallStateChanged?.invoke(session)

        val payload = encodeSignaling(
            kind = "answer",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = session.callerUhid,
            selectedCodec = session.selectedCodec,
            sampleRateHz = session.sampleRateHz
        )
        sender.send(signalingPacket(session.callerUhid, payload), session.callerUhid)
    }

    /**
     * Hang up or cancel [callId]. Sends a "hangup" or "cancel" to the remote peer.
     */
    suspend fun hangUp(callId: UUID) {
        val session = sessions.remove(callId) ?: return
        val kind = if (session.state == VoiceCallState.Outgoing) "cancel" else "hangup"
        val remote = if (sender.localUhid == session.callerUhid) session.calleeUhid else session.callerUhid
        session.state = VoiceCallState.Ended
        onCallStateChanged?.invoke(session)

        val payload = encodeSignaling(
            kind = kind,
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = remote
        )
        sender.send(signalingPacket(remote, payload), remote)
    }

    /**
     * Send an encoded audio frame for an active call.
     */
    suspend fun sendFrame(callId: UUID, encodedAudio: ByteArray, isSilence: Boolean) {
        val session = sessions[callId]
        if (session == null || session.state != VoiceCallState.Connected) return
        val remote = if (sender.localUhid == session.callerUhid) session.calleeUhid else session.callerUhid

        val seq = frameSequence++.toUInt()
        val payload = encodeVoiceFrame(callId, seq, System.currentTimeMillis(), isSilence, encodedAudio)
        val packet = MeshPacket(
            type = PacketType.VoiceCall,
            sourceUhid = sender.localUhid,
            destinationUhid = remote,
            ttl = AetherMeshConstants.DEFAULT_TTL,
            priority = 64,
            payload = payload
        )
        sender.send(packet, remote)
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

    private fun handleSignaling(packet: MeshPacket) {
        val json = String(packet.payload, Charsets.UTF_8)
        val kind = JsonReader.readString(json, "kind") ?: return
        val callIdStr = JsonReader.readString(json, "call_id") ?: return
        val callId = runCatching { UUID.fromString(callIdStr) }.getOrNull() ?: return
        val fromUhid = JsonReader.readString(json, "from_uhid") ?: return

        when (kind) {
            "offer" -> {
                val codecs = JsonReader.readStringArray(json, "proposed_codecs")
                val rate = JsonReader.readInt(json, "sample_rate_hz") ?: 16000
                val session = VoiceCallSession(
                    id = callId,
                    callerUhid = fromUhid,
                    calleeUhid = sender.localUhid,
                    state = VoiceCallState.Incoming,
                    proposedCodecs = codecs,
                    sampleRateHz = rate
                )
                sessions[callId] = session
                onIncomingCall?.invoke(session)
            }
            "answer" -> {
                val session = sessions[callId] ?: return
                session.state = VoiceCallState.Connected
                session.selectedCodec = JsonReader.readString(json, "selected_codec")
                onCallStateChanged?.invoke(session)
            }
            "hangup", "cancel", "timeout" -> {
                val session = sessions.remove(callId) ?: return
                session.state = VoiceCallState.Ended
                onCallStateChanged?.invoke(session)
            }
        }
    }

    private fun handleFrame(packet: MeshPacket) {
        val data = packet.payload
        if (data.size < 29) return  // 16 + 4 + 8 + 1 = minimum 29 bytes
        val buf = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)

        // UUID is big-endian (first 16 bytes)
        val uuidBuf = ByteBuffer.wrap(data, 0, 16).order(ByteOrder.BIG_ENDIAN)
        val callId = UUID(uuidBuf.long, uuidBuf.long)
        buf.position(16)

        @Suppress("UNUSED_VARIABLE") val seq = buf.int.toUInt()
        @Suppress("UNUSED_VARIABLE") val ts = buf.long
        val isSilence = buf.get() != 0.toByte()
        val encoded = if (buf.hasRemaining()) {
            val arr = ByteArray(buf.remaining())
            buf.get(arr)
            arr
        } else ByteArray(0)

        onFrameReceived?.invoke(callId, encoded, isSilence)
    }

    private fun signalingPacket(toUhid: String, payload: ByteArray) = MeshPacket(
        type = PacketType.VoiceSignaling,
        sourceUhid = sender.localUhid,
        destinationUhid = toUhid,
        ttl = AetherMeshConstants.DEFAULT_TTL,
        priority = 32,
        payload = payload
    )
}

// ─────────────────────────────────────────────────────────────────────────────
// Wire helpers
// ─────────────────────────────────────────────────────────────────────────────

internal fun encodeVoiceFrame(
    callId: UUID,
    sequence: UInt,
    timestampMs: Long,
    isSilence: Boolean,
    encodedPayload: ByteArray
): ByteArray {
    val buf = ByteBuffer.allocate(16 + 4 + 8 + 1 + encodedPayload.size)
    buf.order(ByteOrder.BIG_ENDIAN)
    buf.putLong(callId.mostSignificantBits)
    buf.putLong(callId.leastSignificantBits)
    buf.order(ByteOrder.LITTLE_ENDIAN)
    buf.putInt(sequence.toInt())
    buf.putLong(timestampMs)
    buf.put(if (isSilence) 1.toByte() else 0.toByte())
    buf.put(encodedPayload)
    return buf.array()
}

internal fun encodeSignaling(
    kind: String,
    callId: UUID,
    fromUhid: String,
    toUhid: String,
    proposedCodecs: List<String>? = null,
    selectedCodec: String? = null,
    sampleRateHz: Int? = null,
    reason: String? = null
): ByteArray {
    val sb = StringBuilder()
    sb.append('{')
    sb.append("\"kind\":\"").append(jsonEscape(kind)).append("\",")
    sb.append("\"call_id\":\"").append(callId).append("\",")
    sb.append("\"from_uhid\":\"").append(jsonEscape(fromUhid)).append("\",")
    sb.append("\"to_uhid\":\"").append(jsonEscape(toUhid)).append('"')
    if (proposedCodecs != null) {
        sb.append(",\"proposed_codecs\":[")
        proposedCodecs.forEachIndexed { i, c ->
            if (i > 0) sb.append(',')
            sb.append('"').append(jsonEscape(c)).append('"')
        }
        sb.append(']')
    }
    if (selectedCodec != null) sb.append(",\"selected_codec\":\"").append(jsonEscape(selectedCodec)).append('"')
    if (sampleRateHz != null) sb.append(",\"sample_rate_hz\":").append(sampleRateHz)
    if (reason != null) sb.append(",\"reason\":\"").append(jsonEscape(reason)).append('"')
    sb.append('}')
    return sb.toString().toByteArray(Charsets.UTF_8)
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared JSON utilities (package-private)
// ─────────────────────────────────────────────────────────────────────────────

internal object JsonReader {
    fun readString(json: String, key: String): String? {
        val needle = "\"$key\":"
        val i = json.indexOf(needle)
        if (i < 0) return null
        var p = i + needle.length
        while (p < json.length && json[p].isWhitespace()) p++
        if (p >= json.length || json[p] != '"') return null
        p++
        val sb = StringBuilder()
        while (p < json.length) {
            val c = json[p]
            if (c == '\\' && p + 1 < json.length) {
                when (json[p + 1]) {
                    'n' -> sb.append('\n')
                    'r' -> sb.append('\r')
                    't' -> sb.append('\t')
                    '"' -> sb.append('"')
                    '\\' -> sb.append('\\')
                    else -> sb.append(json[p + 1])
                }
                p += 2
            } else if (c == '"') {
                return sb.toString()
            } else {
                sb.append(c)
                p++
            }
        }
        return null
    }

    fun readInt(json: String, key: String): Int? = readLong(json, key)?.toInt()

    fun readLong(json: String, key: String): Long? {
        val needle = "\"$key\":"
        val i = json.indexOf(needle)
        if (i < 0) return null
        var p = i + needle.length
        while (p < json.length && json[p].isWhitespace()) p++
        val start = p
        while (p < json.length && (json[p].isDigit() || json[p] == '-')) p++
        if (p == start) return null
        return json.substring(start, p).toLongOrNull()
    }

    fun readDouble(json: String, key: String): Double? {
        val needle = "\"$key\":"
        val i = json.indexOf(needle)
        if (i < 0) return null
        var p = i + needle.length
        while (p < json.length && json[p].isWhitespace()) p++
        val start = p
        while (p < json.length && (json[p].isDigit() || json[p] == '-' || json[p] == '.' || json[p] == 'E' || json[p] == 'e' || json[p] == '+')) p++
        if (p == start) return null
        return json.substring(start, p).toDoubleOrNull()
    }

    fun readBool(json: String, key: String): Boolean? {
        val needle = "\"$key\":"
        val i = json.indexOf(needle)
        if (i < 0) return null
        var p = i + needle.length
        while (p < json.length && json[p].isWhitespace()) p++
        if (p + 4 <= json.length && json.regionMatches(p, "true", 0, 4)) return true
        if (p + 5 <= json.length && json.regionMatches(p, "false", 0, 5)) return false
        return null
    }

    fun readStringArray(json: String, key: String): List<String> {
        val needle = "\"$key\":["
        val i = json.indexOf(needle)
        if (i < 0) return emptyList()
        val start = i + needle.length
        val end = json.indexOf(']', start)
        if (end <= start) return emptyList()
        val body = json.substring(start, end).trim()
        if (body.isEmpty()) return emptyList()
        val result = mutableListOf<String>()
        var p = 0
        while (p < body.length) {
            while (p < body.length && body[p] != '"') p++
            if (p >= body.length) break
            p++
            val sb = StringBuilder()
            while (p < body.length) {
                val c = body[p]
                if (c == '\\' && p + 1 < body.length) { sb.append(body[p + 1]); p += 2 }
                else if (c == '"') { p++; break }
                else { sb.append(c); p++ }
            }
            result.add(sb.toString())
            while (p < body.length && body[p] != '"' && body[p] != ']') p++
        }
        return result
    }
}

internal fun jsonEscape(s: String): String {
    val sb = StringBuilder()
    for (c in s) {
        when (c) {
            '\\' -> sb.append("\\\\")
            '"' -> sb.append("\\\"")
            '\n' -> sb.append("\\n")
            '\r' -> sb.append("\\r")
            '\t' -> sb.append("\\t")
            else -> sb.append(c)
        }
    }
    return sb.toString()
}
