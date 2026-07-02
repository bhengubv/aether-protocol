// SPDX-License-Identifier: MIT

// Wire binding for AetherNet presence (PresenceBeacon 21 + PresenceQuery 22). A
// privacy-preserving "I'm here" broadcast: the beacon advertises the node's ROTATING
// erid (from EridDirectory — never the stable UHID), a COARSE geohash (host-truncated;
// empty when hidden), its capability bitmask, a presence status, and a send timestamp.
// The query solicits beacon replies for a (possibly empty) geohash. Transport only —
// the ERID rotation + geohash coarsening are the host's concern. Port of the C#
// reference (PresenceService).

package aethernet.presence

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader
import java.util.UUID

/**
 * Canonical wire payload for [PacketType.PresenceBeacon] (21) — a privacy-preserving
 * "I'm here" broadcast. Advertises the node's ROTATING [erid] (never the stable UHID),
 * a COARSE [geohash] (empty = hidden), its NodeCapabilities [capabilities] bitmask, a
 * PresenceStatus [status], and a [sentAtMs] send timestamp. Field order: erid, geohash,
 * capabilities, status, sent_at_ms. snake_case keys, capabilities/status as bare Int,
 * sent_at_ms as a bare Long.
 *
 * Built by hand (no kotlinx.serialization — AOSP Soong forbids it) with a StringBuilder
 * so the emitted bytes match the C# reference exactly. Doubles as the
 * [PresenceService.onBeaconReceived] event body. Byte-identity gate:
 * fixtures/presence/vectors.json.
 */
data class PresenceBeaconPayload(
    /** The node's current rotating Ephemeral Routing Id (Crockford base-32). NOT the UHID. */
    val erid: String = "",
    /** Coarse geohash of the node (host-truncated per privacy level); empty string = hidden. */
    val geohash: String = "",
    /** NodeCapabilities bitmask (BLE=1, WifiDirect=2, Gateway=4, Relay=8, …). */
    val capabilities: Int = 0,
    /** PresenceStatus value (Unknown=0, Available=1, Busy=2, Away=3, DoNotDisturb=4, Offline=5). */
    val status: Int = 0,
    /** Unix timestamp (ms) when the beacon was sent. */
    val sentAtMs: Long = 0,
) {
    /** Serialize to the canonical UTF-8 JSON wire bytes. */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"erid\":\"").append(jsonEscape(erid)).append("\",")
        sb.append("\"geohash\":\"").append(jsonEscape(geohash)).append("\",")
        sb.append("\"capabilities\":").append(capabilities).append(',')
        sb.append("\"status\":").append(status).append(',')
        sb.append("\"sent_at_ms\":").append(sentAtMs)
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    companion object {
        /** Parse canonical wire bytes into a beacon, or null if malformed / erid missing. */
        fun fromJson(json: String): PresenceBeaconPayload? {
            val erid = JsonReader.readString(json, "erid") ?: return null
            if (erid.isEmpty()) return null
            return PresenceBeaconPayload(
                erid = erid,
                geohash = JsonReader.readString(json, "geohash") ?: "",
                capabilities = JsonReader.readInt(json, "capabilities") ?: 0,
                status = JsonReader.readInt(json, "status") ?: 0,
                sentAtMs = JsonReader.readLong(json, "sent_at_ms") ?: 0L,
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
 * Canonical wire payload for [PacketType.PresenceQuery] (22) — "who's around here?".
 * Broadcast to solicit [PresenceBeaconPayload] replies. Field order: query_id, geohash.
 * An empty [geohash] means "anywhere". [queryId] serializes as a lowercase-dashed UUID.
 *
 * Built by hand (no kotlinx.serialization — AOSP Soong forbids it) with a StringBuilder
 * so the emitted bytes match the C# reference exactly. Doubles as the
 * [PresenceService.onQueryReceived] event body. Byte-identity gate:
 * fixtures/presence/vectors.json.
 */
data class PresenceQueryPayload(
    val queryId: UUID = UUID(0L, 0L),
    val geohash: String = "",
) {
    /** Serialize to the canonical UTF-8 JSON wire bytes. */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"query_id\":\"").append(queryId.toString()).append("\",")
        sb.append("\"geohash\":\"").append(jsonEscape(geohash)).append('"')
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    companion object {
        /** Parse canonical wire bytes into a query, or null if query_id is missing/unparseable. */
        fun fromJson(json: String): PresenceQueryPayload? {
            val id = JsonReader.readString(json, "query_id") ?: return null
            val uuid = try {
                UUID.fromString(id)
            } catch (_: IllegalArgumentException) {
                return null
            }
            return PresenceQueryPayload(
                queryId = uuid,
                geohash = JsonReader.readString(json, "geohash") ?: "",
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
 * Presence over [PacketType.PresenceBeacon] (21) and [PacketType.PresenceQuery] (22).
 * Broadcast a beacon (the host builds it with the rotating erid + coarse geohash),
 * broadcast a query, and surface inbound beacons/queries via [onBeaconReceived] /
 * [onQueryReceived]. Transport only — the ERID rotation + geohash coarsening are the
 * host's concern (this service never touches the stable UHID or precise location). Uses
 * the in-memory [FakeMeshSender] in tests — no transport needed. Mirrors the C#
 * PresenceService.
 */
class PresenceService(private val sender: MeshSender) {

    /** Raised when a presence beacon arrives from a peer, with the sender's UHID. */
    var onBeaconReceived: ((PresenceBeaconPayload, String) -> Unit)? = null

    /** Raised when a presence query arrives from a peer, with the sender's UHID. */
    var onQueryReceived: ((PresenceQueryPayload, String) -> Unit)? = null

    /**
     * Broadcast a presence [beacon] to mesh peers. Returns the number of peers reached.
     */
    suspend fun broadcastBeacon(beacon: PresenceBeaconPayload): Int {
        val packet = MeshPacket(
            type = PacketType.PresenceBeacon,
            sourceUhid = sender.localUhid,
            destinationUhid = "*",
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = beacon.toJsonBytes(),
        )
        return sender.broadcast(packet)
    }

    /**
     * Broadcast a presence query for the given (coarse, possibly empty) [geohash].
     * Returns the freshly-generated query id.
     */
    suspend fun query(geohash: String): UUID {
        val queryId = UUID.randomUUID()
        val payload = PresenceQueryPayload(queryId = queryId, geohash = geohash)
        val packet = MeshPacket(
            type = PacketType.PresenceQuery,
            sourceUhid = sender.localUhid,
            destinationUhid = "*",
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload.toJsonBytes(),
        )
        sender.broadcast(packet)
        return queryId
    }

    /**
     * Process an inbound presence packet (beacon or query). Returns false on wrong type
     * or malformed payload (an empty erid for a beacon counts as malformed); on success
     * fires the matching event and returns true.
     */
    suspend fun handle(packet: MeshPacket): Boolean {
        return when (packet.type) {
            PacketType.PresenceBeacon -> {
                val beacon = PresenceBeaconPayload.fromJson(packet.payload.toString(Charsets.UTF_8)) ?: return false
                onBeaconReceived?.invoke(beacon, packet.sourceUhid)
                true
            }
            PacketType.PresenceQuery -> {
                val q = PresenceQueryPayload.fromJson(packet.payload.toString(Charsets.UTF_8)) ?: return false
                onQueryReceived?.invoke(q, packet.sourceUhid)
                true
            }
            else -> false
        }
    }
}
