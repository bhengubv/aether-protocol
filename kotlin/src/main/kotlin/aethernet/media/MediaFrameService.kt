// SPDX-License-Identifier: MIT

// Wire bindings for the VoicePtt(15) + ScreenShare(32) directed media frames. Both share the exact
// 29-byte header used by the existing VoiceCall(16)/VideoFrame(31) frames, so a node can treat them
// uniformly. Port of the C# reference (AetherNet.Core/Media/MediaFrameService.cs). BINARY frames —
// no JSON, no kotlinx.serialization.

package aethernet.media

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

/** A push-to-talk audio frame ([PacketType.VoicePtt] = 15 body). */
data class VoicePttFrame(
    val callId: UUID,
    val sequence: UInt,
    val timestampMs: Long,
    val isSilence: Boolean = false,
    val encodedPayload: ByteArray = ByteArray(0),
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is VoicePttFrame) return false
        return callId == other.callId &&
            sequence == other.sequence &&
            timestampMs == other.timestampMs &&
            isSilence == other.isSilence &&
            encodedPayload.contentEquals(other.encodedPayload)
    }

    override fun hashCode(): Int {
        var result = callId.hashCode()
        result = 31 * result + sequence.hashCode()
        result = 31 * result + timestampMs.hashCode()
        result = 31 * result + isSilence.hashCode()
        result = 31 * result + encodedPayload.contentHashCode()
        return result
    }
}

/** A screen-share video frame ([PacketType.ScreenShare] = 32 body). */
data class ScreenShareFrame(
    val callId: UUID,
    val sequence: UInt,
    val timestampMs: Long,
    val isKeyframe: Boolean = false,
    val encodedPayload: ByteArray = ByteArray(0),
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ScreenShareFrame) return false
        return callId == other.callId &&
            sequence == other.sequence &&
            timestampMs == other.timestampMs &&
            isKeyframe == other.isKeyframe &&
            encodedPayload.contentEquals(other.encodedPayload)
    }

    override fun hashCode(): Int {
        var result = callId.hashCode()
        result = 31 * result + sequence.hashCode()
        result = 31 * result + timestampMs.hashCode()
        result = 31 * result + isKeyframe.hashCode()
        result = 31 * result + encodedPayload.contentHashCode()
        return result
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Binary codec
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Binary codec for the VoicePtt(15) and ScreenShare(32) media frames. Both share the exact 29-byte
 * header used by the existing VoiceCall(16)/VideoFrame(31) frames:
 *   [0..15]  call_id       — 16 bytes, RFC-4122 BIG-ENDIAN (most-significant then least-significant
 *                            bits as big-endian longs — network order, NOT the .NET mixed-endian
 *                            Guid.ToByteArray() layout)
 *   [16..19] sequence      — u32 LITTLE-ENDIAN
 *   [20..27] timestamp_ms  — i64 LITTLE-ENDIAN
 *   [28]     flag          — u8 (VoicePtt: is_silence; ScreenShare: is_keyframe)
 *   [29..]   payload       — opaque encoded audio/video bytes
 * Byte-identity gate: fixtures/media/vectors.json (expected_hex).
 */
object MediaFrameCodec {
    const val HEADER_LENGTH = 29

    fun serializeVoicePtt(f: VoicePttFrame): ByteArray =
        serialize(f.callId, f.sequence, f.timestampMs, f.isSilence, f.encodedPayload)

    fun serializeScreenShare(f: ScreenShareFrame): ByteArray =
        serialize(f.callId, f.sequence, f.timestampMs, f.isKeyframe, f.encodedPayload)

    private fun serialize(
        callId: UUID,
        sequence: UInt,
        timestampMs: Long,
        flag: Boolean,
        payload: ByteArray,
    ): ByteArray {
        val buf = ByteBuffer.allocate(HEADER_LENGTH + payload.size)
        // call_id: RFC-4122 big-endian (msb then lsb as big-endian longs)
        buf.order(ByteOrder.BIG_ENDIAN)
        buf.putLong(callId.mostSignificantBits)
        buf.putLong(callId.leastSignificantBits)
        // sequence + timestamp: little-endian
        buf.order(ByteOrder.LITTLE_ENDIAN)
        buf.putInt(sequence.toInt())
        buf.putLong(timestampMs)
        buf.put(if (flag) 1.toByte() else 0.toByte())
        buf.put(payload)
        return buf.array()
    }

    /** @throws IllegalArgumentException if [b] is shorter than the 29-byte header. */
    fun deserializeVoicePtt(b: ByteArray): VoicePttFrame {
        require(b.size >= HEADER_LENGTH) { "VoicePtt frame too short" }
        val (callId, sequence, timestampMs, flag, payload) = decode(b)
        return VoicePttFrame(callId, sequence, timestampMs, flag, payload)
    }

    /** @throws IllegalArgumentException if [b] is shorter than the 29-byte header. */
    fun deserializeScreenShare(b: ByteArray): ScreenShareFrame {
        require(b.size >= HEADER_LENGTH) { "ScreenShare frame too short" }
        val (callId, sequence, timestampMs, flag, payload) = decode(b)
        return ScreenShareFrame(callId, sequence, timestampMs, flag, payload)
    }

    private data class Header(
        val callId: UUID,
        val sequence: UInt,
        val timestampMs: Long,
        val flag: Boolean,
        val payload: ByteArray,
    )

    private fun decode(b: ByteArray): Header {
        // call_id: first 16 bytes, big-endian
        val uuidBuf = ByteBuffer.wrap(b, 0, 16).order(ByteOrder.BIG_ENDIAN)
        val callId = UUID(uuidBuf.long, uuidBuf.long)
        // sequence + timestamp + flag: little-endian, starting at offset 16
        val buf = ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN)
        buf.position(16)
        val sequence = buf.int.toUInt()
        val timestampMs = buf.long
        val flag = buf.get() != 0.toByte()
        val payload = if (buf.hasRemaining()) {
            val arr = ByteArray(buf.remaining())
            buf.get(arr)
            arr
        } else {
            ByteArray(0)
        }
        return Header(callId, sequence, timestampMs, flag, payload)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Inbound event args
// ─────────────────────────────────────────────────────────────────────────────

/** An inbound VoicePtt frame plus the peer that sent it. */
data class VoicePttFrameReceived(val frame: VoicePttFrame, val fromUhid: String)

/** An inbound ScreenShare frame plus the peer that sent it. */
data class ScreenShareFrameReceived(val frame: ScreenShareFrame, val fromUhid: String)

// ─────────────────────────────────────────────────────────────────────────────
// Services
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Binds [PacketType.VoicePtt] (15) to the mesh: directed push-to-talk audio frames + inbound event.
 * Uses the in-memory [FakeMeshSender] in tests — no transport needed. Mirrors the C# VoicePttService.
 */
class VoicePttService(private val sender: MeshSender) {

    /** Raised when a VoicePtt frame arrives from a peer (frame + sender UHID). */
    var onFrameReceived: ((VoicePttFrameReceived) -> Unit)? = null

    /**
     * Send [frame] as a directed VoicePtt(15) packet to [peerUhid]. Returns delivery success.
     * Throws if [peerUhid] is empty.
     */
    suspend fun sendFrame(peerUhid: String, frame: VoicePttFrame): Boolean {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        val packet = MeshPacket(
            type = PacketType.VoicePtt,
            sourceUhid = sender.localUhid,
            destinationUhid = peerUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = MediaFrameCodec.serializeVoicePtt(frame),
        )
        return sender.send(packet, peerUhid)
    }

    /**
     * Process an inbound [PacketType.VoicePtt]. Returns false on wrong type or a malformed (too
     * short) frame; on success fires [onFrameReceived] and returns true.
     */
    suspend fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.VoicePtt) return false
        if (packet.payload.size < MediaFrameCodec.HEADER_LENGTH) return false
        val frame = MediaFrameCodec.deserializeVoicePtt(packet.payload)
        onFrameReceived?.invoke(VoicePttFrameReceived(frame, packet.sourceUhid))
        return true
    }
}

/**
 * Binds [PacketType.ScreenShare] (32) to the mesh: directed screen-share video frames + inbound
 * event. Uses the in-memory [FakeMeshSender] in tests — no transport needed. Mirrors the C#
 * ScreenShareService.
 */
class ScreenShareService(private val sender: MeshSender) {

    /** Raised when a ScreenShare frame arrives from a peer (frame + sender UHID). */
    var onFrameReceived: ((ScreenShareFrameReceived) -> Unit)? = null

    /**
     * Send [frame] as a directed ScreenShare(32) packet to [peerUhid]. Returns delivery success.
     * Throws if [peerUhid] is empty.
     */
    suspend fun sendFrame(peerUhid: String, frame: ScreenShareFrame): Boolean {
        require(peerUhid.isNotEmpty()) { "peerUhid must not be empty" }
        val packet = MeshPacket(
            type = PacketType.ScreenShare,
            sourceUhid = sender.localUhid,
            destinationUhid = peerUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = MediaFrameCodec.serializeScreenShare(frame),
        )
        return sender.send(packet, peerUhid)
    }

    /**
     * Process an inbound [PacketType.ScreenShare]. Returns false on wrong type or a malformed (too
     * short) frame; on success fires [onFrameReceived] and returns true.
     */
    suspend fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.ScreenShare) return false
        if (packet.payload.size < MediaFrameCodec.HEADER_LENGTH) return false
        val frame = MediaFrameCodec.deserializeScreenShare(packet.payload)
        onFrameReceived?.invoke(ScreenShareFrameReceived(frame, packet.sourceUhid))
        return true
    }
}
