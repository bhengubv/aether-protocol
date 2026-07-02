// SPDX-License-Identifier: MIT

// Wire binding for aether-vault shard requests (Phase-2 extension). Binds
// PacketType.VaultShardRequest (42) to the mesh: ask peers for an erasure-coded
// shard by hash, and surface inbound requests via onShardRequested (the host answers
// from IVaultService if it holds the shard). Port of the C# reference
// (VaultShardRequestService).

package aethernet.vaultshard

import aethernet.AetherNetConstants
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import aethernet.routing.MeshSender
import aethernet.voice.JsonReader

/**
 * A request for a single erasure-coded shard the requester needs to recover a file.
 * Doubles as the canonical wire payload for [PacketType.VaultShardRequest] (42) and
 * the [VaultShardRequestService.onShardRequested] event arg. Field order: shard_hash,
 * requester_uhid. snake_case keys, no whitespace.
 *
 * Built by hand (no kotlinx.serialization — AOSP Soong forbids it) with a
 * StringBuilder so the emitted bytes match the C# reference exactly. Byte-identity
 * gate: fixtures/vaultshard/vectors.json.
 */
data class VaultShardRequest(
    val shardHash: String = "",
    val requesterUhid: String = "",
) {
    /** Serialize to the canonical UTF-8 JSON wire bytes. */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"shard_hash\":\"").append(jsonEscape(shardHash)).append("\",")
        sb.append("\"requester_uhid\":\"").append(jsonEscape(requesterUhid)).append('"')
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    companion object {
        /** Parse canonical wire bytes into a request, or null if malformed / shard_hash missing. */
        fun fromJson(json: String): VaultShardRequest? {
            val shardHash = JsonReader.readString(json, "shard_hash") ?: return null
            if (shardHash.isEmpty()) return null
            return VaultShardRequest(
                shardHash = shardHash,
                requesterUhid = JsonReader.readString(json, "requester_uhid") ?: "",
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
 * Binds [PacketType.VaultShardRequest] (42) to the mesh: ask peers for a shard, and
 * surface inbound shard requests via [onShardRequested] (the host answers from
 * IVaultService if it holds the shard). Transport for the aether-vault erasure-coded-
 * storage extension. Mirrors the C# VaultShardRequestService.
 */
class VaultShardRequestService(private val sender: MeshSender) {

    /** Raised when a peer requests a shard. */
    var onShardRequested: ((VaultShardRequest) -> Unit)? = null

    /**
     * Broadcast a request for [shardHash] (requester = this node's localUhid). Returns
     * the number of peers reached.
     */
    suspend fun requestShard(shardHash: String): Int {
        require(shardHash.isNotEmpty()) { "shardHash must not be empty" }
        val payload = VaultShardRequest(shardHash = shardHash, requesterUhid = sender.localUhid)
        val packet = MeshPacket(
            type = PacketType.VaultShardRequest,
            sourceUhid = sender.localUhid,
            destinationUhid = "*",
            ttl = AetherNetConstants.DEFAULT_TTL,
            payload = payload.toJsonBytes(),
        )
        return sender.broadcast(packet)
    }

    /**
     * Process an inbound [PacketType.VaultShardRequest]. Returns false on wrong type or
     * malformed payload; on success fires [onShardRequested] and returns true.
     */
    suspend fun handle(packet: MeshPacket): Boolean {
        if (packet.type != PacketType.VaultShardRequest) return false
        val body = VaultShardRequest.fromJson(packet.payload.toString(Charsets.UTF_8)) ?: return false
        onShardRequested?.invoke(body)
        return true
    }
}
