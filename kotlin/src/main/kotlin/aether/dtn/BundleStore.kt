// SPDX-License-Identifier: MIT

package aether.dtn

import aether.models.BundleStatus
import aether.models.CustodyRecord
import aether.models.DtnBundle
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/** Persistent backing store for DTN bundles + custody records. */
interface BundleStore {
    suspend fun get(bundleId: UUID): DtnBundle?
    suspend fun getActive(): List<DtnBundle>
    suspend fun save(bundle: DtnBundle)
    suspend fun remove(bundleId: UUID)
    suspend fun getActiveCount(): Int
    suspend fun saveCustody(record: CustodyRecord)
    suspend fun getCustodyRecords(bundleId: UUID): List<CustodyRecord>
    suspend fun expireStale(): Int
}

/** Process-local DTN store. Suitable for tests. */
class InMemoryBundleStore : BundleStore {
    private val bundles = ConcurrentHashMap<UUID, DtnBundle>()
    private val custody = ConcurrentHashMap<UUID, CustodyRecord>()

    override suspend fun get(bundleId: UUID): DtnBundle? = bundles[bundleId]

    override suspend fun getActive(): List<DtnBundle> =
        bundles.values.filter {
            !it.isExpired() && (it.status == "Pending" || it.status == "InCustody")
        }

    override suspend fun save(bundle: DtnBundle) {
        bundles[bundle.id] = bundle
    }

    override suspend fun remove(bundleId: UUID) {
        bundles.remove(bundleId)
    }

    override suspend fun getActiveCount(): Int = getActive().size

    override suspend fun saveCustody(record: CustodyRecord) {
        custody[record.id] = record
    }

    override suspend fun getCustodyRecords(bundleId: UUID): List<CustodyRecord> =
        custody.values.filter { it.bundleId == bundleId }

    override suspend fun expireStale(): Int {
        // Existing DtnBundle.status is a String — we can't mutate the val; we replace.
        var expired = 0
        val now = Instant.now()
        for (b in bundles.values.toList()) {
            if (now.isAfter(b.expiresAt) && b.status != "Expired") {
                bundles[b.id] = b.copy(status = "Expired")
                expired++
            }
        }
        return expired
    }
}
