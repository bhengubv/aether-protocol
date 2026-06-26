// SPDX-License-Identifier: MIT

// aether-forge: a mesh-native package cache proxy (Phase-2 extension). The first
// internet pull of a package is cached as Aether content; subsequent pulls by
// anyone in the mesh are served locally at mesh speeds. Port of the C# reference
// (AetherNet.Forge). Ecosystems: npm, pip, cargo, go, nuget, git.

package aethernet.forge

import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/** Metadata record for one cached package artifact. */
data class ForgeEntry(
    var contentHash: String = "",
    var packageId: String = "", // "ecosystem:name@version", e.g. "npm:react@18.2.0"
    var fetchedAtUtc: Instant = Instant.now(),
    var sizeBytes: Long = 0,
    var downloadCount: Int = 0,
)

/** Aggregate statistics for the local Forge cache. */
data class ForgeStats(
    var totalBytesSaved: Long = 0,
    var totalPeersServed: Int = 0,
    var catalogueSize: Int = 0,
    var topPackages: List<ForgeEntry> = emptyList(),
)

/** The mesh-native package cache. */
interface IForgeService {
    suspend fun query(packageId: String): ForgeEntry?

    /** Store a new artifact (idempotent — first write wins). */
    suspend fun cache(packageId: String, contentHash: String, sizeBytes: Long): ForgeEntry

    /** Increment the download counter and return the entry, or null if not cached. */
    suspend fun fetch(packageId: String): ForgeEntry?

    suspend fun getStats(): ForgeStats
}

/** In-memory [IForgeService] for testing / single-node use; state lost on restart. */
class InMemoryForgeService : IForgeService {
    private val store = ConcurrentHashMap<String, ForgeEntry>() // key = packageId

    /** Fires when a new artifact is added via [cache]. */
    var onNewEntryAnnounced: ((ForgeEntry) -> Unit)? = null

    override suspend fun query(packageId: String): ForgeEntry? = store[packageId]

    override suspend fun cache(packageId: String, contentHash: String, sizeBytes: Long): ForgeEntry {
        var isNew = false
        val entry = store.getOrPut(packageId) {
            isNew = true
            ForgeEntry(
                packageId = packageId,
                contentHash = contentHash,
                sizeBytes = sizeBytes,
                fetchedAtUtc = Instant.now(),
                downloadCount = 0,
            )
        }
        if (isNew) onNewEntryAnnounced?.invoke(entry)
        return entry
    }

    override suspend fun fetch(packageId: String): ForgeEntry? {
        val entry = store[packageId] ?: return null
        synchronized(entry) { entry.downloadCount++ }
        return entry
    }

    override suspend fun getStats(): ForgeStats {
        val entries = store.values.toList()
        val totalBytesSaved = entries.sumOf { it.downloadCount.toLong() * it.sizeBytes }
        val topPackages = entries.sortedByDescending { it.downloadCount }.take(10)
        return ForgeStats(
            totalBytesSaved = totalBytesSaved,
            totalPeersServed = 0,
            catalogueSize = entries.size,
            topPackages = topPackages,
        )
    }
}
