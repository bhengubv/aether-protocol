// SPDX-License-Identifier: MIT

package aethernet.routing

import aethernet.AetherNetConstants
import aethernet.extensibility.IncentiveProvider
import aethernet.extensibility.NoopIncentiveProvider
import aethernet.models.RouteEntry
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.security.NodeReputationService
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * AODV-inspired reactive routing service.
 *
 * Lifecycle:
 *   - findRoute(destinationUhid) — returns cached or discovers via RREQ/RREP.
 *   - handleRouteRequest / handleRouteReply — pump received RREQ / RREP.
 *   - prune() — clear expired routes and trim RREQ dedup state.
 */
class RoutingService(
    private val sender: MeshSender,
    private val store: RouteStore = InMemoryRouteStore(),
    private val verifier: RouteReplyVerifier = AcceptAllRouteReplyVerifier(),
    private val incentives: IncentiveProvider = NoopIncentiveProvider()
) {
    private val cache = ConcurrentHashMap<String, RouteEntry>()
    private val pending = ConcurrentHashMap<String, CompletableDeferred<RouteEntry?>>()
    private val seenRreqs: MutableSet<UUID> = ConcurrentHashMap.newKeySet()
    private val loadLock = Mutex()
    @Volatile private var loaded = false

    // Per-source RREQ rate-limit state (System.currentTimeMillis() epoch-ms timestamps)
    private val rreqSources = ConcurrentHashMap<String, MutableList<Long>>()
    @Volatile private var reputation: NodeReputationService? = null

    suspend fun findRoute(destinationUhid: String): RouteEntry? {
        require(destinationUhid.isNotEmpty()) { "destinationUhid must not be empty" }
        ensureLoaded()

        cache[destinationUhid]?.takeIf { !it.isExpired() }?.let { return it }

        val stored = store.get(destinationUhid)
        if (stored != null && !stored.isExpired()) {
            cache[destinationUhid] = stored
            return stored
        }

        return discover(destinationUhid)
    }

    fun getCachedRoute(destinationUhid: String): RouteEntry? {
        if (destinationUhid.isEmpty()) return null
        return cache[destinationUhid]?.takeIf { !it.isExpired() }
    }

    fun getAllRoutes(): List<RouteEntry> = cache.values.filter { !it.isExpired() }

    /** Attach an optional NodeReputationService. Pass null to disable. */
    fun setReputation(rep: NodeReputationService?) {
        reputation = rep
    }

    suspend fun handleRouteRequest(rreq: MeshPacket) {
        require(rreq.type == PacketType.RouteRequest) { "expected PacketType.RouteRequest" }
        // Dedup: if already seen, drop
        if (seenRreqs.contains(rreq.id)) return
        // Per-source RREQ rate limiting — mirrors Go/Rust RoutingService.
        val nowMs = System.currentTimeMillis()
        val windowStart = nowMs - AetherNetConstants.RREQ_RATE_LIMIT_WINDOW_MS
        val src = rreq.sourceUhid
        val existing = rreqSources.getOrPut(src) { java.util.Collections.synchronizedList(mutableListOf()) }
        synchronized(existing) {
            existing.removeIf { it <= windowStart }
            if (existing.size >= AetherNetConstants.RREQ_RATE_LIMIT_MAX) {
                reputation?.recordRreqFloodAttempt(src)
                return  // drop: source is flooding unique RREQs
            }
            existing.add(nowMs)
        }
        // Add to dedup cache (only reached if not a flood packet)
        if (!seenRreqs.add(rreq.id)) return  // race: another coroutine beat us to it — drop

        val local = sender.localUhid
        if (rreq.sourceUhid.isEmpty() || rreq.sourceUhid == local) return

        val hopCount = (AetherNetConstants.DEFAULT_TTL - rreq.ttl + 1).coerceAtLeast(1)
        val reverse = RouteEntry(
            destinationUhid = rreq.sourceUhid,
            nextHopUhid = rreq.sourceUhid,
            hopCount = hopCount,
            qualityScore = 50,
            expiresAt = Instant.now().plusSeconds(AetherNetConstants.ROUTE_EXPIRY_SECONDS)
        )
        cache[reverse.destinationUhid] = reverse
        store.save(reverse)

        if (rreq.destinationUhid == local) {
            sendRouteReply(local, rreq)
            return
        }

        val known = cache[rreq.destinationUhid]
        if (known != null && !known.isExpired()) {
            sendRouteReply(rreq.destinationUhid, rreq)
            return
        }

        if (rreq.ttl > 1) {
            rreq.ttl -= 1
            sender.broadcast(rreq)
            incentives.recordRelay(local, rreq)
        }
    }

    suspend fun handleRouteReply(rrep: MeshPacket) {
        require(rrep.type == PacketType.RouteReply) { "expected PacketType.RouteReply" }
        if (!verifier.verify(rrep)) return

        val local = sender.localUhid
        if (rrep.sourceUhid.isEmpty() || rrep.sourceUhid == local) return

        val hopCount = (AetherNetConstants.DEFAULT_TTL - rrep.ttl + 1).coerceAtLeast(1)
        val forward = RouteEntry(
            destinationUhid = rrep.sourceUhid,
            nextHopUhid = rrep.sourceUhid,
            hopCount = hopCount,
            qualityScore = 50,
            expiresAt = Instant.now().plusSeconds(AetherNetConstants.ROUTE_EXPIRY_SECONDS)
        )
        cache[forward.destinationUhid] = forward
        store.save(forward)

        if (rrep.destinationUhid == local) {
            pending.remove(forward.destinationUhid)?.complete(forward)
            return
        }

        if (rrep.ttl <= 1) return

        val next = cache[rrep.destinationUhid]?.takeIf { !it.isExpired() } ?: return
        rrep.ttl -= 1
        if (sender.send(rrep, next.nextHopUhid)) incentives.recordRelay(local, rrep)
    }

    suspend fun prune() {
        cache.entries.removeIf { it.value.isExpired() }
        if (seenRreqs.size > 10_000) seenRreqs.clear()
        store.pruneExpired()
    }

    private suspend fun sendRouteReply(repliedSource: String, rreq: MeshPacket) {
        val rrep = MeshPacket(
            type = PacketType.RouteReply,
            sourceUhid = repliedSource,
            destinationUhid = rreq.sourceUhid,
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = rreq.payload
        )
        val reverse = cache[rreq.sourceUhid]?.takeIf { !it.isExpired() }
        if (reverse != null) {
            sender.send(rrep, reverse.nextHopUhid)
        } else {
            sender.broadcast(rrep)
        }
    }

    private suspend fun discover(destinationUhid: String): RouteEntry? {
        val deferred = CompletableDeferred<RouteEntry?>()
        pending[destinationUhid] = deferred

        val rreq = MeshPacket(
            type = PacketType.RouteRequest,
            sourceUhid = sender.localUhid,
            destinationUhid = destinationUhid,
            ttl = AetherNetConstants.DEFAULT_TTL
        )
        val fanout = sender.broadcast(rreq)
        if (fanout == 0) {
            pending.remove(destinationUhid)
            return null
        }

        return try {
            withTimeoutOrNull(AetherNetConstants.ROUTE_TIMEOUT_MS) { deferred.await() }
        } catch (_: TimeoutCancellationException) {
            null
        } finally {
            pending.remove(destinationUhid)
        }
    }

    private suspend fun ensureLoaded() {
        if (loaded) return
        loadLock.withLock {
            if (loaded) return
            loaded = true
            try {
                for (r in store.getAll()) {
                    if (!r.isExpired()) cache[r.destinationUhid] = r
                }
            } catch (_: Exception) {
                loaded = false
            }
        }
    }
}
