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

enum class StreamState { Live, Ended }

data class StreamInfo(
    val streamId: UUID,
    val publisherUhid: String,
    val title: String,
    val contentType: String,
    val codec: String,
    val segmentDurationMs: Int,
    var state: StreamState = StreamState.Live,
    val startedAtMs: Long = System.currentTimeMillis()
)

/**
 * Live-streaming service for audio/video segment distribution over the mesh.
 *
 * Wire format — StreamSegment binary payload:
 *   [16] StreamId      (UUID RFC4122 big-endian)
 *   [4]  Sequence      (UInt32 little-endian)
 *   [8]  TimestampMs   (Int64 little-endian)
 *   [1]  IsKeyframe    (0 or 1)
 *   [N]  EncodedPayload
 *
 * Signaling packets use JSON (snake_case).
 */
class StreamingService(private val sender: MeshSender) {

    /** Active streams this node is publishing. */
    private val activeStreams = ConcurrentHashMap<UUID, StreamInfo>()

    /**
     * Subscriber map: streamId → set of subscriber UHIDs.
     * Access via ConcurrentHashMap for thread safety.
     */
    private val subscribers = ConcurrentHashMap<UUID, MutableSet<String>>()

    @Volatile private var segmentSequence: Int = 0

    /** Fired when a stream announce arrives from a remote publisher. */
    var onStreamAnnounced: ((StreamInfo) -> Unit)? = null

    /** Fired when a stream ends (state → Ended). */
    var onStreamEnded: ((UUID) -> Unit)? = null

    /** Fired when a segment arrives for a subscribed stream. */
    var onSegmentReceived: ((UUID, ByteArray, Boolean) -> Unit)? = null

    // ─────────────────────────────────────────────────────────────────────
    // Publisher API
    // ─────────────────────────────────────────────────────────────────────

    /**
     * Start a new stream and announce it to the mesh.
     */
    suspend fun startStream(
        title: String,
        contentType: String,
        codec: String,
        segmentDurationMs: Int
    ): UUID {
        require(title.isNotEmpty()) { "title must not be empty" }
        val streamId = UUID.randomUUID()
        val info = StreamInfo(
            streamId = streamId,
            publisherUhid = sender.localUhid,
            title = title,
            contentType = contentType,
            codec = codec,
            segmentDurationMs = segmentDurationMs,
            state = StreamState.Live
        )
        activeStreams[streamId] = info
        subscribers[streamId] = ConcurrentHashMap.newKeySet()

        val payload = encodeStreamAnnounce(info)
        val packet = MeshPacket(
            type = PacketType.StreamAnnounce,
            sourceUhid = sender.localUhid,
            destinationUhid = "",
            ttl = AetherNetConstants.DEFAULT_TTL,
            priority = 32,
            payload = payload
        )
        sender.broadcast(packet)
        return streamId
    }

    /**
     * End the stream and announce to subscribers.
     */
    suspend fun endStream(streamId: UUID) {
        val info = activeStreams.remove(streamId) ?: return
        info.state = StreamState.Ended
        subscribers.remove(streamId)

        val payload = encodeStreamAnnounce(info)
        val packet = MeshPacket(
            type = PacketType.StreamAnnounce,
            sourceUhid = sender.localUhid,
            destinationUhid = "",
            ttl = AetherNetConstants.DEFAULT_TTL,
            priority = 32,
            payload = payload
        )
        sender.broadcast(packet)
        onStreamEnded?.invoke(streamId)
    }

    /**
     * Subscribe to a remote stream. Sends a subscribe request to [publisherUhid].
     */
    suspend fun subscribe(streamId: UUID, publisherUhid: String, liveOnly: Boolean) {
        require(publisherUhid.isNotEmpty()) { "publisherUhid must not be empty" }
        val payload = encodeStreamSubscribe(streamId, liveOnly)
        val packet = MeshPacket(
            type = PacketType.StreamSubscribe,
            sourceUhid = sender.localUhid,
            destinationUhid = publisherUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            priority = 32,
            payload = payload
        )
        sender.send(packet, publisherUhid)
    }

    /**
     * Unsubscribe from a remote stream.
     */
    suspend fun unsubscribe(streamId: UUID, publisherUhid: String) {
        require(publisherUhid.isNotEmpty()) { "publisherUhid must not be empty" }
        val payload = encodeStreamUnsubscribe(streamId)
        val packet = MeshPacket(
            type = PacketType.StreamUnsubscribe,
            sourceUhid = sender.localUhid,
            destinationUhid = publisherUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            priority = 32,
            payload = payload
        )
        sender.send(packet, publisherUhid)
    }

    /**
     * Publish a new segment to all current subscribers of [streamId].
     */
    suspend fun publishSegment(streamId: UUID, data: ByteArray, isKeyframe: Boolean) {
        if (!activeStreams.containsKey(streamId)) return
        val subs = subscribers[streamId] ?: return

        val seq = segmentSequence++.toUInt()
        val payload = encodeStreamSegment(streamId, seq, System.currentTimeMillis(), isKeyframe, data)
        val packet = MeshPacket(
            type = PacketType.StreamSegment,
            sourceUhid = sender.localUhid,
            destinationUhid = "",
            ttl = AetherNetConstants.DEFAULT_TTL,
            priority = 48,
            payload = payload
        )
        for (sub in subs) {
            sender.send(packet.copy(destinationUhid = sub), sub)
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Inbound packet dispatcher
    // ─────────────────────────────────────────────────────────────────────

    suspend fun handlePacket(packet: MeshPacket) {
        when (packet.type) {
            PacketType.StreamAnnounce -> handleAnnounce(packet)
            PacketType.StreamSubscribe -> handleSubscribe(packet)
            PacketType.StreamUnsubscribe -> handleUnsubscribe(packet)
            PacketType.StreamSegment -> handleSegment(packet)
            else -> {}
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────

    private fun handleAnnounce(packet: MeshPacket) {
        val json = String(packet.payload, Charsets.UTF_8)
        val streamIdStr = JsonReader.readString(json, "stream_id") ?: return
        val streamId = runCatching { UUID.fromString(streamIdStr) }.getOrNull() ?: return
        val title = JsonReader.readString(json, "title") ?: ""
        val contentType = JsonReader.readString(json, "content_type") ?: ""
        val codec = JsonReader.readString(json, "codec") ?: ""
        val segmentMs = JsonReader.readInt(json, "segment_duration_ms") ?: AetherNetConstants.DEFAULT_SEGMENT_DURATION_MS.toInt()
        val stateStr = JsonReader.readString(json, "state") ?: "live"
        val startedAtMs = JsonReader.readLong(json, "started_at_ms") ?: System.currentTimeMillis()
        val state = if (stateStr == "ended") StreamState.Ended else StreamState.Live

        val info = StreamInfo(
            streamId = streamId,
            publisherUhid = packet.sourceUhid,
            title = title,
            contentType = contentType,
            codec = codec,
            segmentDurationMs = segmentMs,
            state = state,
            startedAtMs = startedAtMs
        )
        if (state == StreamState.Ended) {
            onStreamEnded?.invoke(streamId)
        } else {
            onStreamAnnounced?.invoke(info)
        }
    }

    private fun handleSubscribe(packet: MeshPacket) {
        val json = String(packet.payload, Charsets.UTF_8)
        val streamIdStr = JsonReader.readString(json, "stream_id") ?: return
        val streamId = runCatching { UUID.fromString(streamIdStr) }.getOrNull() ?: return
        if (!activeStreams.containsKey(streamId)) return

        subscribers.getOrPut(streamId) { ConcurrentHashMap.newKeySet() }
            .add(packet.sourceUhid)
    }

    private fun handleUnsubscribe(packet: MeshPacket) {
        val json = String(packet.payload, Charsets.UTF_8)
        val streamIdStr = JsonReader.readString(json, "stream_id") ?: return
        val streamId = runCatching { UUID.fromString(streamIdStr) }.getOrNull() ?: return
        subscribers[streamId]?.remove(packet.sourceUhid)
    }

    private fun handleSegment(packet: MeshPacket) {
        val data = packet.payload
        // Minimum: 16 + 4 + 8 + 1 = 29 bytes
        if (data.size < 29) return

        val uuidBuf = ByteBuffer.wrap(data, 0, 16).order(ByteOrder.BIG_ENDIAN)
        val streamId = UUID(uuidBuf.long, uuidBuf.long)

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

        onSegmentReceived?.invoke(streamId, encoded, isKeyframe)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Wire helpers
// ─────────────────────────────────────────────────────────────────────────────

internal fun encodeStreamSegment(
    streamId: UUID,
    sequence: UInt,
    timestampMs: Long,
    isKeyframe: Boolean,
    encodedPayload: ByteArray
): ByteArray {
    val buf = ByteBuffer.allocate(16 + 4 + 8 + 1 + encodedPayload.size)
    buf.order(ByteOrder.BIG_ENDIAN)
    buf.putLong(streamId.mostSignificantBits)
    buf.putLong(streamId.leastSignificantBits)
    buf.order(ByteOrder.LITTLE_ENDIAN)
    buf.putInt(sequence.toInt())
    buf.putLong(timestampMs)
    buf.put(if (isKeyframe) 1.toByte() else 0.toByte())
    buf.put(encodedPayload)
    return buf.array()
}

private fun encodeStreamAnnounce(info: StreamInfo): ByteArray {
    val sb = StringBuilder()
    sb.append('{')
    sb.append("\"stream_id\":\"").append(info.streamId).append("\",")
    sb.append("\"title\":\"").append(jsonEscape(info.title)).append("\",")
    sb.append("\"content_type\":\"").append(jsonEscape(info.contentType)).append("\",")
    sb.append("\"codec\":\"").append(jsonEscape(info.codec)).append("\",")
    sb.append("\"segment_duration_ms\":").append(info.segmentDurationMs).append(',')
    sb.append("\"state\":\"").append(if (info.state == StreamState.Live) "live" else "ended").append("\",")
    sb.append("\"started_at_ms\":").append(info.startedAtMs)
    sb.append('}')
    return sb.toString().toByteArray(Charsets.UTF_8)
}

private fun encodeStreamSubscribe(streamId: UUID, liveOnly: Boolean): ByteArray {
    val s = "{\"stream_id\":\"$streamId\",\"live_only\":$liveOnly}"
    return s.toByteArray(Charsets.UTF_8)
}

private fun encodeStreamUnsubscribe(streamId: UUID): ByteArray {
    val s = "{\"stream_id\":\"$streamId\"}"
    return s.toByteArray(Charsets.UTF_8)
}
