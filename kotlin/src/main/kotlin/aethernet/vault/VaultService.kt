// SPDX-License-Identifier: MIT

// In-memory aether-vault service (Phase-2 extension): erasure-coded distributed
// backup over this package's ReedSolomon vault codec. Port of the C# reference
// (AetherNet.Vault.InMemoryVaultService) — K=10 / M=4, shard layout byte-identical
// so a shard set produced here is decodable by any other node.

package aethernet.vault

import java.security.MessageDigest
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

const val VAULT_K = 10
const val VAULT_M = 4

/** The only thing the owner must retain to reconstruct a vaulted file. */
data class VaultManifest(
    val contentHash: String,
    val shardHashes: List<String>,
    val k: Int,
    val m: Int,
    val sizeBytes: Long,
    val label: String,
    val createdAtUtc: Instant,
) {
    val totalShards: Int get() = k + m
}

/** A current reachability report for a vaulted file. */
data class VaultHealth(
    val totalShards: Int,
    val reachableShards: Int,
    val isRecoverable: Boolean,
    val redundancyScore: Double,
)

/** The aether-vault erasure-coded backup store. */
interface IVaultService {
    suspend fun store(data: ByteArray, label: String): VaultManifest
    suspend fun recover(manifest: VaultManifest): ByteArray
    fun checkHealth(manifest: VaultManifest): VaultHealth
    suspend fun replicate(manifest: VaultManifest, targetRedundancy: Int = 14)
}

private fun sha256Hex(data: ByteArray): String =
    MessageDigest.getInstance("SHA-256").digest(data).joinToString("") { "%02x".format(it) }

/** In-memory [IVaultService] for testing / single-node use; shards lost on restart. */
class InMemoryVaultService : IVaultService {
    private val shards = ConcurrentHashMap<String, ByteArray>() // shard hash -> bytes

    override suspend fun store(data: ByteArray, label: String): VaultManifest {
        val contentHash = sha256Hex(data)
        val codec = ReedSolomonCodec(VAULT_K, VAULT_M)
        val shardArr = if (data.isEmpty()) {
            // Empty file: K zero-padded 1-byte data shards (mirrors the C# shardSize = 1 case).
            codec.encode(Array(VAULT_K) { ByteArray(1) })
        } else {
            codec.encodeData(data)
        }
        val shardHashes = shardArr.map { sh ->
            val h = sha256Hex(sh)
            shards[h] = sh
            h
        }
        return VaultManifest(contentHash, shardHashes, VAULT_K, VAULT_M, data.size.toLong(), label, Instant.now())
    }

    override suspend fun recover(manifest: VaultManifest): ByteArray {
        val total = manifest.shardHashes.size
        val k = manifest.k
        val m = total - k
        val codec = ReedSolomonCodec(k, m)
        val available = HashMap<Int, ByteArray>()
        manifest.shardHashes.forEachIndexed { i, h -> shards[h]?.let { available[i] = it } }
        require(available.size >= k) { "vault: cannot recover — only ${available.size}/$k shards available" }
        return codec.reconstructData(available, manifest.sizeBytes.toInt())
    }

    override fun checkHealth(manifest: VaultManifest): VaultHealth {
        val reachable = manifest.shardHashes.count { shards.containsKey(it) }
        val total = manifest.totalShards
        return VaultHealth(
            totalShards = total,
            reachableShards = reachable,
            isRecoverable = reachable >= manifest.k,
            redundancyScore = if (total > 0) reachable.toDouble() / total else 0.0,
        )
    }

    override suspend fun replicate(manifest: VaultManifest, targetRedundancy: Int) {
        // No-op in the in-memory implementation.
    }
}
