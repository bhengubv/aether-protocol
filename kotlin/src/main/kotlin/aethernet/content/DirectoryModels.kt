// SPDX-License-Identifier: MIT

package aethernet.content

import org.json.JSONObject
import java.time.Instant
import java.util.UUID

/**
 * Wire payload for [aethernet.protocol.PacketType.NamePublish]. Serialized as
 * JSON with snake_case property names for cross-language interop with the
 * C# / Python / Go / Rust / Swift / TypeScript implementations.
 *
 * Two modes:
 * - Unsolicited broadcast: emitted by the publisher on
 *   [DirectoryService.publish]. [inResponseToQueryId] is null.
 * - Query response: a peer that holds the name emits this in unicast back to
 *   the querier. [inResponseToQueryId] carries the originating query's
 *   correlation id (UUID stringified — matches `Guid?` on the wire in C#).
 *
 * Added in v1.2.0 (Issue #60).
 *
 * Hand-rolled JSON codec (buildString encode, [org.json.JSONObject] decode) so
 * the type compiles under AOSP Soong without the kotlinx-serialization plugin.
 * Canonical key order: name, descriptor, in_response_to_query_id — byte-identical
 * to the C# reference and `fixtures/expected/name_publish_*.bin`.
 */
data class NamePublishPayload(
    /** The application-layer name being announced. Wire key: `name`. */
    val name: String = "",

    /** The full descriptor that the name resolves to. Wire key: `descriptor` (nested object). */
    val descriptor: ContentDescriptor = ContentDescriptor(),

    /**
     * If non-null, this is a unicast response to a prior NameQuery whose
     * `query_id` matched this value. If null, the publish is unsolicited.
     * Serialized as a UUID string (the wire-stable representation of `Guid?`),
     * or the JSON literal `null`. Wire key: `in_response_to_query_id`.
     */
    val inResponseToQueryId: String? = null,
) {
    /** Canonical wire JSON. Fixed key order — see class doc. */
    fun toJson(): String = buildString {
        append("{\"name\":"); appendJsonString(name)
        append(",\"descriptor\":"); descriptor.appendJsonTo(this)
        append(",\"in_response_to_query_id\":")
        if (inResponseToQueryId == null) append("null") else appendJsonString(inResponseToQueryId)
        append('}')
    }

    companion object {
        /** Parse from canonical JSON. Returns null on malformed input. */
        fun fromJson(json: String): NamePublishPayload? = try {
            val o = JSONObject(json)
            val descObj = o.optJSONObject("descriptor")
            NamePublishPayload(
                name = o.optString("name", ""),
                descriptor = if (descObj != null) ContentDescriptor.fromJsonObject(descObj) else ContentDescriptor(),
                inResponseToQueryId = if (o.isNull("in_response_to_query_id")) null
                                      else o.getString("in_response_to_query_id"),
            )
        } catch (_: Exception) {
            null
        }
    }
}

/**
 * Wire payload for [aethernet.protocol.PacketType.NameQuery]. A broadcast
 * request asking peers to send a [NamePublishPayload] for the named entry
 * back to the sender, correlated by [queryId].
 *
 * Added in v1.2.0 (Issue #60).
 *
 * Hand-rolled JSON codec (Soong-compatible). Canonical key order: name, query_id.
 */
data class NameQueryPayload(
    /** The application-layer name being queried. Wire key: `name`. */
    val name: String = "",

    /** Correlation id. Echoed by responders in
     *  [NamePublishPayload.inResponseToQueryId] so the querier can match
     *  responses to outstanding queries. Wire key: `query_id`. */
    val queryId: String = UUID.randomUUID().toString(),
) {
    /** Canonical wire JSON. Fixed key order: name, query_id. */
    fun toJson(): String = buildString {
        append("{\"name\":"); appendJsonString(name)
        append(",\"query_id\":"); appendJsonString(queryId)
        append('}')
    }

    companion object {
        /** Parse from canonical JSON. Returns null on malformed input. */
        fun fromJson(json: String): NameQueryPayload? = try {
            val o = JSONObject(json)
            NameQueryPayload(
                name = o.optString("name", ""),
                queryId = if (o.has("query_id")) o.optString("query_id", "")
                          else UUID.randomUUID().toString(),
            )
        } catch (_: Exception) {
            null
        }
    }
}

/**
 * Event payload raised by [DirectoryService.entryAnnounced] when a NamePublish
 * packet arrives and the local catalogue learns a new (or replaced) name ->
 * descriptor binding.
 *
 * Added in v1.2.0 (Issue #60).
 */
data class DirectoryEntryAnnouncedEvent(
    /** The newly-learned application-layer name. */
    val name: String,

    /** The descriptor the name resolves to. */
    val descriptor: ContentDescriptor,

    /** UHID of the peer that emitted the announcement. */
    val sourceUhid: String,

    /** UTC time the announcement arrived locally. */
    val announcedAtUtc: Instant = Instant.now(),
)
