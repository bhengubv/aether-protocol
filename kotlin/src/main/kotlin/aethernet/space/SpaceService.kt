// SPDX-License-Identifier: MIT

// aether-space: geo-pinned community noticeboards (Phase-2 extension). Nodes drop
// breadcrumbs at geohash coordinates; passing devices auto-pull and re-host them
// for other passersby — fully offline. Port of the C# reference (AetherNet.Space).
// Wire format: JSON, transmitted as PacketType.SpaceBreadcrumb (40).

package aethernet.space

import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.concurrent.ConcurrentHashMap

/** Category of a geo-pinned breadcrumb. */
enum class BreadcrumbType(val value: Int) {
    NOTICE(0),
    EMERGENCY(1),
    COMMERCE(2),
    EVENT(3),
    JOB_POSTING(4);

    companion object {
        fun fromValue(v: Int): BreadcrumbType = entries.first { it.value == v }
    }
}

const val EMERGENCY_TTL_HOURS = 720
const val MIN_TTL_HOURS = 1
const val MAX_TTL_HOURS = 168

/**
 * A geo-pinned digital notice dropped at a physical location. Content is
 * addressed by hash; the breadcrumb carries only metadata. Plain class (not a
 * data class) because of the ByteArray signature field.
 */
class SpaceBreadcrumb(
    var contentHash: String = "",
    var geoHash: String = "",
    var anchorUhid: String = "",
    var createdAtUtc: Instant = Instant.now(),
    var ttlHours: Int = 72,
    var type: BreadcrumbType = BreadcrumbType.NOTICE,
    var signature: ByteArray = ByteArray(0),
) {
    /** UTC expiry = createdAtUtc + ttlHours. */
    val expiresAtUtc: Instant
        get() = createdAtUtc.plus(ttlHours.toLong(), ChronoUnit.HOURS)

    /** True once the breadcrumb's TTL has passed. */
    val isExpired: Boolean
        get() = !Instant.now().isBefore(expiresAtUtc)
}

/** The aether-space breadcrumb store. */
interface ISpaceService {
    suspend fun drop(
        geoHash: String,
        contentHash: String,
        anchorUhid: String,
        type: BreadcrumbType = BreadcrumbType.NOTICE,
        ttlHours: Int = 72,
    ): SpaceBreadcrumb

    suspend fun scan(centerGeoHash: String, radiusCells: Int = 1): List<SpaceBreadcrumb>

    suspend fun pin(breadcrumb: SpaceBreadcrumb)

    /** Creator-only delete: succeeds only if [requestorUhid] is the breadcrumb's anchorUhid. */
    suspend fun delete(breadcrumb: SpaceBreadcrumb, requestorUhid: String): Boolean

    /** Drops every expired breadcrumb; returns the count removed. */
    fun pruneExpired(): Int
}

private fun clamp(value: Int, lo: Int, hi: Int): Int = if (value < lo) lo else if (value > hi) hi else value

/**
 * In-memory [ISpaceService] for testing / single-node use; state lost on restart.
 * Proximity matching uses a geohash-prefix heuristic.
 */
class InMemorySpaceService : ISpaceService {
    private val store = ConcurrentHashMap<String, SpaceBreadcrumb>() // key = contentHash

    /** Fires when a breadcrumb is dropped locally or pinned from the mesh. */
    var onBreadcrumbReceived: ((SpaceBreadcrumb) -> Unit)? = null

    /** Fires when a cached breadcrumb passes its TTL. */
    var onBreadcrumbExpired: ((SpaceBreadcrumb) -> Unit)? = null

    override suspend fun drop(
        geoHash: String,
        contentHash: String,
        anchorUhid: String,
        type: BreadcrumbType,
        ttlHours: Int,
    ): SpaceBreadcrumb {
        val effectiveTtl =
            if (type == BreadcrumbType.EMERGENCY) EMERGENCY_TTL_HOURS
            else clamp(ttlHours, MIN_TTL_HOURS, MAX_TTL_HOURS)
        val crumb = SpaceBreadcrumb(
            contentHash = contentHash,
            geoHash = geoHash,
            anchorUhid = anchorUhid,
            createdAtUtc = Instant.now(),
            ttlHours = effectiveTtl,
            type = type,
        )
        store[contentHash] = crumb
        onBreadcrumbReceived?.invoke(crumb)
        return crumb
    }

    override suspend fun scan(centerGeoHash: String, radiusCells: Int): List<SpaceBreadcrumb> {
        // Prefix-based proximity: match the first (6 - radiusCells) chars.
        val prefixLen = clamp(6 - radiusCells, 1, 6)
        val prefix =
            (if (centerGeoHash.length >= prefixLen) centerGeoHash.substring(0, prefixLen) else centerGeoHash)
                .lowercase()
        return store.values.filter { !it.isExpired && it.geoHash.lowercase().startsWith(prefix) }
    }

    override suspend fun pin(breadcrumb: SpaceBreadcrumb) {
        store[breadcrumb.contentHash] = breadcrumb
        onBreadcrumbReceived?.invoke(breadcrumb)
    }

    override suspend fun delete(breadcrumb: SpaceBreadcrumb, requestorUhid: String): Boolean {
        val stored = store[breadcrumb.contentHash] ?: return false
        if (stored.anchorUhid != requestorUhid) return false // creator-only delete
        return store.remove(breadcrumb.contentHash) != null
    }

    override fun pruneExpired(): Int {
        val expired = store.values.filter { it.isExpired }
        for (crumb in expired) {
            if (store.remove(crumb.contentHash) != null) {
                onBreadcrumbExpired?.invoke(crumb)
            }
        }
        return expired.size
    }
}
