// SPDX-License-Identifier: MIT

package aethernet.streaming

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import aethernet.voice.jsonEscape
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

enum class VideoCallState { Outgoing, Incoming, Connected, Ended, Failed }

data class VideoCallSession(
    val id: UUID,
    val callerUhid: String,
    val calleeUhid: String,
    var state: VideoCallState,
    val proposedCodecs: List<String> = emptyList(),
    var selectedCodec: String? = null,
    var width: Int = 0,
    var height: Int = 0,
    var fps: Int = 0,
    var bitrateKbps: Int = 0
)

/**
 * One-to-one video call service.
 *
 * Wire format — VideoFrame binary payload:
 *   [16] CallId      (UUID RFC4122 big-endian)
 *   [4]  Sequence    (UInt32 little-endian)
 *   [8]  TimestampMs (Int64 little-endian)
 *   [1]  IsKeyframe  (0 or 1)
 *   [N]  EncodedPayload
 *
 * Signaling uses VideoSignalingMessage JSON (snake_case).
 *
 * Priority: 64 for video frames, 32 for signaling.
 */
class VideoCallService(private val sender: MeshSender) {

    private val sessions = ConcurrentHashMap<UUID, VideoCallSession>()
    @Volatile private var frameSequence: Int = 0

    /** Fired when an incoming video call offer arrives. */
    var onIncomingCall: ((VideoCallSession) -> Unit)? = null

    /** Fired when call state changes (answered, hung up, failed). */
    var onCallStateChanged: ((VideoCallSession) -> Unit)? = null

    /** Fired when a video frame arrives. */
    var onFrameReceived: ((UUID, ByteArray, Boolean) -> Unit)? = null

    /** Fired when the remote peer requests a keyframe. */
    var onKeyframeRequested: ((UUID) -> Unit)? = null

    /** Fired when the remote peer notifies a quality change. */
    var onQualityChanged: ((UUID, Int, Int, Int, Int) -> Unit)? = null

    // ─────────────────────────────────────────────────────────────────────
    // Outbound API
    // ─────────────────────────────────────────────────────────────────────

    /**
     * Initiate a video call to [toUhid]. Returns the new call-id.
     */
    suspend fun sendOffer(
        toUhid: String,
        codecs: List<String>,
        width: Int,
        height: Int,
        fps: Int,
        bitrateKbps: Int
    ): UUID {
        require(toUhid.isNotEmpty()) { "toUhid must not be empty" }
        val callId = UUID.randomUUID()
        val session = VideoCallSession(
            id = callId,
            callerUhid = sender.localUhid,
            calleeUhid = toUhid,
            state = VideoCallState.Outgoing,
            proposedCodecs = codecs,
            width = width,
            height = height,
            fps = fps,
            bitrateKbps = bitrateKbps
        )
        sessions[callId] = session

        val payload = encodeVideoSignaling(
            kind = "offer",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = toUhid,
            proposedCodecs = codecs,
            width = width,
            height = height,
            fps = fps,
            bitrateKbps = bitrateKbps
        )
        sender.send(videoSignalingPacket(toUhid, payload), toUhid)
        return callId
    }

    /**
     * Accept an incoming call. Sends an "answer" signaling message.
     */
    suspend fun acceptCall(callId: UUID) {
        val session = sessions[callId] ?: return
        if (session.state != VideoCallState.Incoming) return
        session.state = VideoCallState.Connected
        onCallStateChanged?.invoke(session)

        val payload = encodeVideoSignaling(
            kind = "answer",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = session.callerUhid,
            selectedCodec = session.selectedCodec,
            width = session.width,
            height = session.height,
            fps = session.fps,
            bitrateKbps = session.bitrateKbps
        )
        sender.send(videoSignalingPacket(session.callerUhid, payload), session.callerUhid)
    }

    /**
     * Hang up or cancel the call.
     */
    suspend fun hangUp(callId: UUID) {
        val session = sessions.remove(callId) ?: return
        val kind = if (session.state == VideoCallState.Outgoing) "cancel" else "hangup"
        val remote = if (sender.localUhid == session.callerUhid) session.calleeUhid else session.callerUhid
        session.state = VideoCallState.Ended
        onCallStateChanged?.invoke(session)

        val payload = encodeVideoSignaling(
            kind = kind,
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = remote
        )
        sender.send(videoSignalingPacket(remote, payload), remote)
    }

    /**
     * Send an encoded video frame for an active call.
     */
    suspend fun sendFrame(callId: UUID, encodedVideo: ByteArray, isKeyframe: Boolean) {
        val session = sessions[callId]
        if (session == null || session.state != VideoCallState.Connected) return
        val remote = if (sender.localUhid == session.callerUhid) session.calleeUhid else session.callerUhid

        val seq = frameSequence++.toUInt()
        val payload = encodeVideoFrame(callId, seq, System.currentTimeMillis(), isKeyframe, encodedVideo)
        val packet = MeshPacket(
            type = PacketType.VideoFrame,
            sourceUhid = sender.localUhid,
            destinationUhid = remote,
            ttl = AetherNetConstants.DEFAULT_TTL,
            priority = 64,
            payload = payload
        )
        sender.send(packet, remote)
    }

    /**
     * Request the remote peer send a keyframe (for decoder recovery).
     */
    suspend fun requestKeyframe(callId: UUID) {
        val session = sessions[callId] ?: return
        val remote = if (sender.localUhid == session.callerUhid) session.calleeUhid else session.callerUhid

        val payload = encodeVideoSignaling(
            kind = "keyframe_request",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = remote
        )
        sender.send(videoSignalingPacket(remote, payload), remote)
    }

    /**
     * Notify the remote peer of a local quality/resolution change.
     */
    suspend fun notifyQualityChange(callId: UUID, width: Int, height: Int, fps: Int, bitrateKbps: Int) {
        val session = sessions[callId] ?: return
        val remote = if (sender.localUhid == session.callerUhid) session.calleeUhid else session.callerUhid
        session.width = width
        session.height = height
        session.fps = fps
        session.bitrateKbps = bitrateKbps

        val payload = encodeVideoSignaling(
            kind = "quality_change",
            callId = callId,
            fromUhid = sender.localUhid,
            toUhid = remote,
            width = width,
            height = height,
            fps = fps,
            bitrateKbps = bitrateKbps
        )
        sender.send(videoSignalingPacket(remote, payload), remote)
    }

    // ─────────────────────────────────────────────────────────────────────
    // Inbound packet dispatcher
    // ─────────────────────────────────────────────────────────────────────

    suspend fun handlePacket(packet: MeshPacket) {
        when (packet.type) {
            PacketType.VideoSignaling -> handleSignaling(packet)
            PacketType.VideoFrame -> handleFrame(packet)
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
                val w = JsonReader.readInt(json, "width") ?: 0
                val h = JsonReader.readInt(json, "height") ?: 0
                val f = JsonReader.readInt(json, "fps") ?: 0
                val b = JsonReader.readInt(json, "bitrate_kbps") ?: 0
                val session = VideoCallSession(
                    id = callId,
                    callerUhid = fromUhid,
                    calleeUhid = sender.localUhid,
                    state = VideoCallState.Incoming,
                    proposedCodecs = codecs,
                    width = w,
                    height = h,
                    fps = f,
                    bitrateKbps = b
                )
                sessions[callId] = session
                onIncomingCall?.invoke(session)
            }
            "answer" -> {
                val session = sessions[callId] ?: return
                session.state = VideoCallState.Connected
                session.selectedCodec = JsonReader.readString(json, "selected_codec")
                onCallStateChanged?.invoke(session)
            }
            "hangup", "cancel", "timeout" -> {
                val session = sessions.remove(callId) ?: return
                session.state = VideoCallState.Ended
                onCallStateChanged?.invoke(session)
            }
            "keyframe_request" -> {
                onKeyframeRequested?.invoke(callId)
            }
            "quality_change" -> {
                val session = sessions[callId] ?: return
                val w = JsonReader.readInt(json, "width") ?: session.width
                val h = JsonReader.readInt(json, "height") ?: session.height
                val f = JsonReader.readInt(json, "fps") ?: session.fps
                val b = JsonReader.readInt(json, "bitrate_kbps") ?: session.bitrateKbps
                session.width = w; session.height = h; session.fps = f; session.bitrateKbps = b
                onQualityChanged?.invoke(callId, w, h, f, b)
            }
        }
    }

    private fun handleFrame(packet: MeshPacket) {
        val data = packet.payload
        // Minimum: 16 + 4 + 8 + 1 = 29 bytes
        if (data.size < 29) return

        val uuidBuf = ByteBuffer.wrap(data, 0, 16).order(ByteOrder.BIG_ENDIAN)
        val callId = UUID(uuidBuf.long, uuidBuf.long)

        val buf = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)
        buf.position(16)
        @Suppress("UNUSED_VARIABLE") val seq = buf.int.toUInt()
        @Suppress("UNUSED_VARIABLE") val ts = buf.long
        val isKeyframe = buf.get() != 0.toByte()
        val encoded = if (buf.hasRemaining()) {
            val arr = ByteArray(buf.remaining())
            buf.get(arr)
            arr
        } else ByteArray(0)

        onFrameReceived?.invoke(callId, encoded, isKeyframe)
    }

    private fun videoSignalingPacket(toUhid: String, payload: ByteArray) = MeshPacket(
        type = PacketType.VideoSignaling,
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

internal fun encodeVideoFrame(
    callId: UUID,
    sequence: UInt,
    timestampMs: Long,
    isKeyframe: Boolean,
    encodedPayload: ByteArray
): ByteArray {
    val buf = ByteBuffer.allocate(16 + 4 + 8 + 1 + encodedPayload.size)
    buf.order(ByteOrder.BIG_ENDIAN)
    buf.putLong(callId.mostSignificantBits)
    buf.putLong(callId.leastSignificantBits)
    buf.order(ByteOrder.LITTLE_ENDIAN)
    buf.putInt(sequence.toInt())
    buf.putLong(timestampMs)
    buf.put(if (isKeyframe) 1.toByte() else 0.toByte())
    buf.put(encodedPayload)
    return buf.array()
}

private fun encodeVideoSignaling(
    kind: String,
    callId: UUID,
    fromUhid: String,
    toUhid: String,
    proposedCodecs: List<String>? = null,
    selectedCodec: String? = null,
    width: Int? = null,
    height: Int? = null,
    fps: Int? = null,
    bitrateKbps: Int? = null,
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
    if (width != null) sb.append(",\"width\":").append(width)
    if (height != null) sb.append(",\"height\":").append(height)
    if (fps != null) sb.append(",\"fps\":").append(fps)
    if (bitrateKbps != null) sb.append(",\"bitrate_kbps\":").append(bitrateKbps)
    if (reason != null) sb.append(",\"reason\":\"").append(jsonEscape(reason)).append('"')
    sb.append('}')
    return sb.toString().toByteArray(Charsets.UTF_8)
}
