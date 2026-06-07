// SPDX-License-Identifier: MIT

package aethernet.content

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * Application-layer name -> [ContentDescriptor] resolver. Closes the Wave-16
 * protocol gap: content-service is content-addressed (root-hash-keyed) —
 * consumers that want to fetch content by an application-layer name (e.g.
 * `podcast:abc123`, `reel:hash`, `album:artist/title`) cannot do so via the
 * content service alone because they do not know the root hash upfront. That's
 * precisely what they're trying to discover.
 *
 * This service maintains a local name catalogue, broadcasts
 * [PacketType.NamePublish] when the local node publishes a binding, emits
 * [PacketType.NameQuery] when the local node needs to resolve an unknown name,
 * and unicasts a [PacketType.NamePublish] response when a peer's query matches
 * an entry we hold.
 *
 * Mirrors the C# `AetherNet.Content.IDirectoryService` / `DirectoryService`.
 * Added in v1.2.0 (Issue #60).
 *
 * Event pattern matches [aethernet.dtn.DtnService] — callback property
 * (`var onEntryAnnounced`) rather than `SharedFlow` so single-threaded host
 * shells stay simple and idiomatic.
 */
interface DirectoryService {
    /**
     * Raised when a [PacketType.NamePublish] packet arrives — either an
     * unsolicited broadcast from a peer or a unicast response to one of our
     * outstanding queries — and updates the local catalogue.
     */
    var onEntryAnnounced: ((DirectoryEntryAnnouncedEvent) -> Unit)?

    /**
     * Store the binding locally and broadcast a [PacketType.NamePublish] to
     * every connected peer. Subsequent [resolve] calls on the local node
     * return the descriptor immediately from the catalogue.
     */
    suspend fun publish(name: String, descriptor: ContentDescriptor)

    /**
     * Resolve a name to its descriptor. Returns the local-catalogue hit
     * immediately if present. Otherwise broadcasts a [PacketType.NameQuery]
     * and awaits a matching [PacketType.NamePublish] response up to
     * [timeoutMs]. Returns null on timeout.
     */
    suspend fun resolve(name: String, timeoutMs: Long = DEFAULT_QUERY_TIMEOUT_MS): ContentDescriptor?

    /** Enumerate every name currently in the local catalogue (snapshot). */
    suspend fun listNames(): List<String>

    /**
     * Pump inbound [PacketType.NamePublish] / [PacketType.NameQuery] packets
     * into the service. Hosts wire this from their transport's receive pump.
     */
    suspend fun handle(packet: MeshPacket)

    companion object {
        /** Default timeout for [resolve] when no value is supplied. */
        const val DEFAULT_QUERY_TIMEOUT_MS: Long = 5_000L
    }
}

/**
 * Default [DirectoryService] implementation. In-process catalogue with
 * broadcast publish, query/response correlation by id, and cancellation-aware
 * wait loops. Persistence is the host's responsibility (rehydrate via
 * [publish] on startup if you want a non-volatile catalogue).
 */
class DefaultDirectoryService(
    private val sender: MeshSender,
) : DirectoryService {

    private val json: Json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    // Local catalogue: name -> descriptor. Names are application-defined
    // opaque identifiers (not case-insensitive labels) so plain
    // ConcurrentHashMap (case-sensitive) is correct.
    private val catalogue: ConcurrentHashMap<String, ContentDescriptor> = ConcurrentHashMap()

    // Outstanding queries keyed by queryId. Completed when a matching
    // NamePublish arrives, or set to null on timeout.
    private val pendingQueries: ConcurrentHashMap<UUID, CompletableDeferred<ContentDescriptor?>> =
        ConcurrentHashMap()

    override var onEntryAnnounced: ((DirectoryEntryAnnouncedEvent) -> Unit)? = null

    override suspend fun publish(name: String, descriptor: ContentDescriptor) {
        require(name.isNotEmpty()) { "name must not be empty" }

        catalogue[name] = descriptor

        val payload = NamePublishPayload(
            name = name,
            descriptor = descriptor,
            inResponseToQueryId = null,
        )
        val packet = MeshPacket(
            type = PacketType.NamePublish,
            sourceUhid = sender.localUhid,
            destinationUhid = "",
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = json.encodeToString(payload).toByteArray(Charsets.UTF_8),
        )
        sender.broadcast(packet)
    }

    override suspend fun resolve(name: String, timeoutMs: Long): ContentDescriptor? {
        require(name.isNotEmpty()) { "name must not be empty" }

        catalogue[name]?.let { return it }

        val queryId = UUID.randomUUID()
        val deferred = CompletableDeferred<ContentDescriptor?>()
        pendingQueries[queryId] = deferred

        return try {
            val query = NameQueryPayload(name = name, queryId = queryId.toString())
            val packet = MeshPacket(
                type = PacketType.NameQuery,
                sourceUhid = sender.localUhid,
                destinationUhid = "",
                ttl = AetherNetConstants.DEFAULT_TTL,
                payload = json.encodeToString(query).toByteArray(Charsets.UTF_8),
            )
            sender.broadcast(packet)

            withTimeoutOrNull(timeoutMs) { deferred.await() }
        } finally {
            pendingQueries.remove(queryId)
        }
    }

    override suspend fun listNames(): List<String> = catalogue.keys.toList()

    override suspend fun handle(packet: MeshPacket) {
        when (packet.type) {
            PacketType.NamePublish -> handlePublish(packet)
            PacketType.NameQuery -> handleQuery(packet)
            else -> {}
        }
    }

    private fun handlePublish(packet: MeshPacket) {
        val body = decode<NamePublishPayload>(packet.payload) ?: return
        if (body.name.isEmpty()) return

        catalogue[body.name] = body.descriptor

        // Query-response correlation.
        body.inResponseToQueryId?.let { queryIdStr ->
            val uuid = tryParseUuid(queryIdStr) ?: return@let
            val pending = pendingQueries.remove(uuid)
            pending?.complete(body.descriptor)
        }

        onEntryAnnounced?.invoke(
            DirectoryEntryAnnouncedEvent(
                name = body.name,
                descriptor = body.descriptor,
                sourceUhid = packet.sourceUhid,
            )
        )
    }

    private suspend fun handleQuery(packet: MeshPacket) {
        val query = decode<NameQueryPayload>(packet.payload) ?: return
        if (query.name.isEmpty()) return

        val descriptor = catalogue[query.name] ?: return  // silently ignore — others may answer

        val response = NamePublishPayload(
            name = query.name,
            descriptor = descriptor,
            inResponseToQueryId = query.queryId,
        )
        val responsePacket = MeshPacket(
            type = PacketType.NamePublish,
            sourceUhid = sender.localUhid,
            destinationUhid = packet.sourceUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = json.encodeToString(response).toByteArray(Charsets.UTF_8),
        )
        sender.send(responsePacket, packet.sourceUhid)
    }

    private inline fun <reified T> decode(bytes: ByteArray): T? = try {
        json.decodeFromString<T>(String(bytes, Charsets.UTF_8))
    } catch (_: Exception) {
        null
    }

    private fun tryParseUuid(s: String): UUID? = try { UUID.fromString(s) } catch (_: Exception) { null }
}
