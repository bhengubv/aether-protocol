// SPDX-License-Identifier: MIT

package aethernet.content

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
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
 */
@Serializable
data class NamePublishPayload(
    /** The application-layer name being announced. */
    val name: String = "",

    /** The full descriptor that the name resolves to. */
    val descriptor: ContentDescriptor = ContentDescriptor(),

    /**
     * If non-null, this is a unicast response to a prior NameQuery whose
     * `query_id` matched this value. If null, the publish is unsolicited.
     * Serialized as a UUID string (the wire-stable representation of `Guid?`).
     */
    @SerialName("in_response_to_query_id")
    val inResponseToQueryId: String? = null,
)

/**
 * Wire payload for [aethernet.protocol.PacketType.NameQuery]. A broadcast
 * request asking peers to send a [NamePublishPayload] for the named entry
 * back to the sender, correlated by [queryId].
 *
 * Added in v1.2.0 (Issue #60).
 */
@Serializable
data class NameQueryPayload(
    /** The application-layer name being queried. */
    val name: String = "",

    /** Correlation id. Echoed by responders in
     *  [NamePublishPayload.inResponseToQueryId] so the querier can match
     *  responses to outstanding queries. */
    @SerialName("query_id")
    val queryId: String = UUID.randomUUID().toString(),
)

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
