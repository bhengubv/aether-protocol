// SPDX-License-Identifier: MIT

// Wire binding for aether-forge announcements (Phase-2 extension). Binds
// PacketType.ForgeAnnounce (41) to the mesh: broadcast a freshly-cached artifact
// announcement, and surface inbound announcements via onAnnounceReceived (the host
// records them in IForgeService). Port of the C# reference (ForgeAnnounceService).

package aethernet.forge

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader

/**
 * Canonical wire payload for [PacketType.ForgeAnnounce] (41) — a node broadcasts
 * this when it caches a new package artifact, so mesh peers with the
 * aethernet.forge/v1 capability learn where the artifact lives. Field order:
 * package_id, content_hash, size_bytes, announced_at_ms. snake_case keys, ms + size
 * as bare integers.
 *
 * Built by hand (no kotlinx.serialization — AOSP Soong forbids it) with a
 * StringBuilder so the emitted bytes match the C# reference exactly. Doubles as the
 * [ForgeAnnounceService.onAnnounceReceived] event arg. Byte-identity gate:
 * fixtures/forge/vectors.json.
 */
data class ForgeAnnouncePayload(
    val packageId: String = "",
    val contentHash: String = "",
    val sizeBytes: Long = 0,
    val announcedAtMs: Long = 0,
) {
    /** Serialize to the canonical UTF-8 JSON wire bytes. */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"package_id\":\"").append(jsonEscape(packageId)).append("\",")
        sb.append("\"content_hash\":\"").append(jsonEscape(contentHash)).append("\",")
        sb.append("\"size_bytes\":").append(sizeBytes).append(',')
        sb.append("\"announced_at_ms\":").append(announcedAtMs)
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    companion object {
        /** Parse canonical wire bytes into a payload, or null if malformed / package_id missing. */
        fun fromJson(json: String): ForgeAnnouncePayload? {
            val packageId = JsonReader.readString(json, "package_id") ?: return null
            if (packageId.isEmpty()) return null
            return ForgeAnnouncePayload(
                packageId = packageId,
                contentHash = JsonReader.readString(json, "content_hash") ?: "",
                sizeBytes = JsonReader.readLong(json, "size_bytes") ?: 0L,
                announcedAtMs = JsonReader.readLong(json, "announced_at_ms") ?: 0L,
            )
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
}

/**
 * Binds [PacketType.ForgeAnnounce] (41) to the mesh: broadcast a freshly-cached
 * artifact announcement, and surface inbound announcements via [onAnnounceReceived]
 * (the host records them in [IForgeService]). Transport for the aether-forge
 * package-cache extension. Mirrors the C# ForgeAnnounceService.
 */
class ForgeAnnounceService(private val sender: MeshSender) {

    /** Raised when a forge announcement arrives from a peer. */
    var onAnnounceReceived: ((ForgeAnnouncePayload) -> Unit)? = null

    /**
     * Announce a cached artifact to mesh peers. Returns the number of peers reached.
     */
    suspend fun broadcast(packageId: String, contentHash: String, sizeBytes: Long, announcedAtMs: Long): Int {
        require(packageId.isNotEmpty()) { "packageId must not be empty" }
        val payload = ForgeAnnouncePayload(
            packageId = packageId,
            contentHash = contentHash,
            sizeBytes = sizeBytes,
            announcedAtMs = announcedAtMs,
        )
        val packet = MeshPacket(
            type = PacketType.ForgeAnnounce,
            sourceUhid = sender.localUhid,
            destinationUhid = "*",
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload.toJsonBytes(),
        )
        return sender.broadcast(packet)
    }

    /**
     * Process an inbound [PacketType.ForgeAnnounce]. Returns false on wrong type or
     * malformed payload; on success fires [onAnnounceReceived] and returns true.
     */
    suspend fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.ForgeAnnounce) return false
        val body = ForgeAnnouncePayload.fromJson(packet.payload.toString(Charsets.UTF_8)) ?: return false
        onAnnounceReceived?.invoke(body)
        return true
    }
}
