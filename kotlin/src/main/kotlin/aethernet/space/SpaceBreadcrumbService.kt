// SPDX-License-Identifier: MIT

// Wire binding for aether-space breadcrumbs (Phase-2 extension). Binds
// PacketType.SpaceBreadcrumb (40) to the mesh: broadcast a locally-dropped
// breadcrumb, and surface inbound breadcrumbs via onBreadcrumbReceived (the host
// pins them into ISpaceService). Port of the C# reference (SpaceBreadcrumbService).

package aethernet.space

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import java.time.Instant
import java.util.Base64

/**
 * Canonical wire payload for [PacketType.SpaceBreadcrumb] (40). Projects a
 * [SpaceBreadcrumb] onto a byte-identical JSON shape: snake_case keys, the UTC
 * creation time as a Unix-ms integer (not ISO-8601), the category enum as a bare
 * integer, and the Ed25519 signature as STANDARD base64 (empty ByteArray -> "").
 * Field order: content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours,
 * type, signature.
 *
 * Built by hand (no kotlinx.serialization — AOSP Soong forbids it) with a
 * StringBuilder so the emitted bytes match the C# reference exactly. Byte-identity
 * gate: fixtures/space/vectors.json.
 */
object SpaceBreadcrumbCodec {

    /** Serialize [b] to the canonical UTF-8 JSON wire bytes. */
    fun toJsonBytes(b: SpaceBreadcrumb): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"content_hash\":\"").append(jsonEscape(b.contentHash)).append("\",")
        sb.append("\"geo_hash\":\"").append(jsonEscape(b.geoHash)).append("\",")
        sb.append("\"anchor_uhid\":\"").append(jsonEscape(b.anchorUhid)).append("\",")
        sb.append("\"created_at_ms\":").append(b.createdAtUtc.toEpochMilli()).append(',')
        sb.append("\"ttl_hours\":").append(b.ttlHours).append(',')
        sb.append("\"type\":").append(b.type.value).append(',')
        sb.append("\"signature\":\"").append(Base64.getEncoder().encodeToString(b.signature)).append('"')
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    /** Parse canonical wire bytes back into a [SpaceBreadcrumb], or null if malformed. */
    fun fromJson(json: String): SpaceBreadcrumb? {
        val contentHash = JsonReader.readString(json, "content_hash") ?: return null
        if (contentHash.isEmpty()) return null
        val createdAtMs = JsonReader.readLong(json, "created_at_ms") ?: 0L
        val sigB64 = JsonReader.readString(json, "signature") ?: ""
        val signature = if (sigB64.isEmpty()) ByteArray(0) else Base64.getDecoder().decode(sigB64)
        return SpaceBreadcrumb(
            contentHash = contentHash,
            geoHash = JsonReader.readString(json, "geo_hash") ?: "",
            anchorUhid = JsonReader.readString(json, "anchor_uhid") ?: "",
            createdAtUtc = Instant.ofEpochMilli(createdAtMs),
            ttlHours = JsonReader.readInt(json, "ttl_hours") ?: 72,
            type = BreadcrumbType.fromValue(JsonReader.readInt(json, "type") ?: 0),
            signature = signature,
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

/**
 * Binds [PacketType.SpaceBreadcrumb] (40) to the mesh: broadcast a locally-dropped
 * breadcrumb, and surface inbound breadcrumbs via [onBreadcrumbReceived] (the host
 * pins them into [ISpaceService]). Transport for the aether-space geo-pinned-notice
 * extension. Uses the in-memory [FakeMeshSender] in tests — no transport needed.
 * Mirrors the C# SpaceBreadcrumbService.
 */
class SpaceBreadcrumbService(private val sender: MeshSender) {

    /** Raised when a breadcrumb arrives from a peer. */
    var onBreadcrumbReceived: ((SpaceBreadcrumb) -> Unit)? = null

    /**
     * Flood [breadcrumb] to mesh peers. Returns the number of peers it was delivered to.
     */
    suspend fun broadcast(breadcrumb: SpaceBreadcrumb): Int {
        val packet = MeshPacket(
            type = PacketType.SpaceBreadcrumb,
            sourceUhid = sender.localUhid,
            destinationUhid = "*",
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = SpaceBreadcrumbCodec.toJsonBytes(breadcrumb),
        )
        return sender.broadcast(packet)
    }

    /**
     * Process an inbound [PacketType.SpaceBreadcrumb]. Returns false on wrong type or
     * malformed payload; on success fires [onBreadcrumbReceived] and returns true.
     */
    suspend fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.SpaceBreadcrumb) return false
        val body = SpaceBreadcrumbCodec.fromJson(packet.payload.toString(Charsets.UTF_8)) ?: return false
        onBreadcrumbReceived?.invoke(body)
        return true
    }
}
