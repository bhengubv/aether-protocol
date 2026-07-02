// SPDX-License-Identifier: MIT

package aethernet.channels

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * JSON payload for [PacketType.ChannelMessage] packets. Wire format: UTF-8 JSON with snake_case keys,
 * field order channel_id, message_id, sender_uhid, content, sent_at_ms, no whitespace, lowercase-dashed
 * UUID, sent_at_ms a bare integer. Byte-identity is locked by fixtures/channels/vectors.json (with ASCII
 * content; escaping of non-ASCII content follows standard JSON).
 *
 * A named channel is an application-layer pub/sub topic ("res-floor-3", a society, a project team).
 * Publishing floods a [ChannelMessage]; nodes subscribed to [channelId] surface it. The original author
 * is carried in [senderUhid] so it survives relay hops (the enclosing packet's sourceUhid changes at
 * each hop).
 *
 * Wire vectors (fixtures/channels/vectors.json), byte-identical with C#:
 *  - channel_id=res-floor-3, message_id=0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f, sender_uhid=aether:alice:01,
 *    content="meeting at 6", sent_at_ms=1700000000000 →
 *    {"channel_id":"res-floor-3","message_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","sender_uhid":"aether:alice:01","content":"meeting at 6","sent_at_ms":1700000000000}
 *  - channel_id=g, message_id=00000000-0000-0000-0000-000000000000, sender_uhid=n, content="",
 *    sent_at_ms=0 →
 *    {"channel_id":"g","message_id":"00000000-0000-0000-0000-000000000000","sender_uhid":"n","content":"","sent_at_ms":0}
 */
data class ChannelMessagePayload(
    /** Application-defined channel identifier (opaque to the protocol). */
    val channelId: String,
    /** Unique id for this message — used for flood de-duplication. */
    val messageId: UUID,
    /** UHID of the original author (preserved across relay hops). */
    val senderUhid: String,
    /** Message body. */
    val content: String,
    /** Unix timestamp in milliseconds when the author published the message. */
    val sentAtMs: Long
) {
    /**
     * Serialize to the canonical UTF-8 JSON wire bytes. Built by hand (no kotlinx.serialization —
     * AOSP Soong forbids it), the same manual string-building approach used by the SOS payload
     * encoder. snake_case keys, field order channel_id, message_id, sender_uhid, content, sent_at_ms,
     * NO whitespace, UUID lowercase-dashed (Java's UUID.toString()), sent_at_ms a bare integer.
     */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"channel_id\":\"").append(jsonEscape(channelId)).append("\",")
        sb.append("\"message_id\":\"").append(messageId).append("\",")
        sb.append("\"sender_uhid\":\"").append(jsonEscape(senderUhid)).append("\",")
        sb.append("\"content\":\"").append(jsonEscape(content)).append("\",")
        sb.append("\"sent_at_ms\":").append(sentAtMs)
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    private fun jsonEscape(s: String): String {
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
}

/**
 * Application-layer named-channel pub/sub over [PacketType.ChannelMessage]. A node subscribes to channel
 * ids it cares about; publishing floods the mesh; subscribed receivers surface the message via
 * [onMessageReceived]. Messages are de-duplicated by [ChannelMessagePayload.messageId] and re-flooded
 * (TTL-bounded) so they reach subscribers several hops away.
 *
 * Mirrors the C# `ChannelMessageService` and the Kotlin [aethernet.sos.SosBroadcastService] /
 * [aethernet.heartbeat.HeartbeatService].
 */
class ChannelMessageService(
    private val sender: MeshSender
) {
    private val subscriptions = ConcurrentHashMap.newKeySet<String>()
    private val seen = ConcurrentHashMap.newKeySet<UUID>()

    /**
     * Raised when a message arrives on a subscribed channel (not raised for this node's own messages).
     * Nullable-lambda event mechanism — matches the other Kotlin services and C#
     * ChannelMessageService.MessageReceived.
     */
    var onMessageReceived: ((ChannelMessagePayload) -> Unit)? = null

    /** Subscribe to a channel — messages on it will raise [onMessageReceived]. */
    fun subscribe(channelId: String) {
        require(channelId.isNotEmpty()) { "channelId must not be empty" }
        subscriptions.add(channelId)
    }

    /** Stop surfacing messages for a channel. */
    fun unsubscribe(channelId: String) {
        subscriptions.remove(channelId)
    }

    /** The channels this node is currently subscribed to. */
    fun getSubscriptions(): List<String> = subscriptions.toList()

    /**
     * Publish [content] to [channelId]: floods a [PacketType.ChannelMessage] to all peers (dest="*",
     * default TTL). Returns the number of peers reached directly.
     */
    suspend fun publish(channelId: String, content: String): Int {
        require(channelId.isNotEmpty()) { "channelId must not be empty" }

        val payload = ChannelMessagePayload(
            channelId = channelId,
            messageId = UUID.randomUUID(),
            senderUhid = sender.localUhid,
            content = content,
            sentAtMs = Instant.now().toEpochMilli()
        )
        seen.add(payload.messageId) // never re-handle our own message when it floods back

        val packet = MeshPacket(
            type = PacketType.ChannelMessage,
            sourceUhid = sender.localUhid,
            destinationUhid = "*",
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload.toJsonBytes()
        )

        return sender.broadcast(packet)
    }

    /**
     * Process an incoming [PacketType.ChannelMessage] packet: de-dup by message id, surface it if we
     * are subscribed to its channel (and it is not our own), and re-flood while TTL allows. Returns
     * false for the wrong packet type, a malformed payload, or a duplicate.
     */
    suspend fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.ChannelMessage) return false

        val json = packet.payload.toString(Charsets.UTF_8)
        val channelId = JsonReader.readString(json, "channel_id")
        if (channelId.isNullOrEmpty()) return false
        val messageId = JsonReader.readString(json, "message_id")?.let {
            runCatching { UUID.fromString(it) }.getOrNull()
        } ?: return false
        val senderUhid = JsonReader.readString(json, "sender_uhid") ?: return false
        val content = JsonReader.readString(json, "content") ?: return false
        val sentAtMs = JsonReader.readLong(json, "sent_at_ms") ?: return false

        // Flood de-duplication: only the first copy of a given message id is processed.
        if (!seen.add(messageId)) return false

        val isOwn = senderUhid == sender.localUhid
        if (!isOwn && subscriptions.contains(channelId)) {
            onMessageReceived?.invoke(
                ChannelMessagePayload(
                    channelId = channelId,
                    messageId = messageId,
                    senderUhid = senderUhid,
                    content = content,
                    sentAtMs = sentAtMs
                )
            )
        }

        // Re-flood so subscribers further out receive it — even if WE aren't subscribed (pure relay).
        if (packet.ttl > 1 && !isOwn) {
            packet.ttl -= 1
            sender.broadcast(packet)
        }

        return true
    }
}
