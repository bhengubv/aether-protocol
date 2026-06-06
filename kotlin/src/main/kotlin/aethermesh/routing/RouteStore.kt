// SPDX-License-Identifier: MIT

package aethermesh.routing

import aethermesh.models.RouteEntry
import java.util.concurrent.ConcurrentHashMap

/**
 * Persistent backing store for the routing table. Default implementation is in-memory;
 * production hosts substitute file- or SQLite-backed implementations for durability.
 */
interface RouteStore {
    suspend fun get(destinationUhid: String): RouteEntry?
    suspend fun getAll(): List<RouteEntry>
    suspend fun save(route: RouteEntry)
    suspend fun remove(destinationUhid: String)
    suspend fun pruneExpired(): Int
}

/** Process-local route store. Loses everything on restart. */
class InMemoryRouteStore : RouteStore {
    private val routes = ConcurrentHashMap<String, RouteEntry>()

    override suspend fun get(destinationUhid: String): RouteEntry? = routes[destinationUhid]

    override suspend fun getAll(): List<RouteEntry> = routes.values.toList()

    override suspend fun save(route: RouteEntry) {
        routes[route.destinationUhid] = route
    }

    override suspend fun remove(destinationUhid: String) {
        routes.remove(destinationUhid)
    }

    override suspend fun pruneExpired(): Int {
        var pruned = 0
        val expired = routes.entries.filter { it.value.isExpired() }
        for ((k, _) in expired) {
            if (routes.remove(k) != null) pruned++
        }
        return pruned
    }
}
