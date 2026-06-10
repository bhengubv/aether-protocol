// SPDX-License-Identifier: MIT

package aethernet.content

import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest

/**
 * Manifest for a piece of chunked content. Identifies the content by a root hash
 * computed over the per-chunk hashes, declares the chunk layout, and lets
 * receivers verify each chunk independently as it arrives.
 *
 * Wire shape (JSON, snake_case): cross-language stable. Producers can publish a
 * descriptor once and any node can pull chunks and verify against it without
 * trusting the sender — content addressing makes the descriptor itself the
 * authority.
 *
 * Mirrors the C# `AetherNet.Content.Models.ContentDescriptor`. Added in v1.2.0
 * (Issue #60 — paired with [aethernet.content.DirectoryService]).
 *
 * Wire codec is hand-rolled (buildString encode, [org.json.JSONObject] decode)
 * rather than kotlinx.serialization so the type compiles under AOSP Soong's
 * plain kotlinc (no serialization compiler plugin). Canonical JSON key order:
 * root_hash, name, total_bytes, chunk_size_bytes, chunk_count, chunk_hashes,
 * content_type, created_at — must stay byte-identical to the C# reference and
 * the `fixtures/expected/name_publish_*.bin` golden vectors.
 */
data class ContentDescriptor(
    /** SHA-256 over the concatenation of all chunk hashes, in order. Hex-encoded lowercase. Wire key: `root_hash`. */
    val rootHash: String = "",

    /** Original file name as the publisher named it. Hint only — never used as a path on the receiver. */
    val name: String = "",

    /** Total size of the original content in bytes. Wire key: `total_bytes`. */
    val totalBytes: Long = 0L,

    /** Bytes per chunk for every chunk except possibly the last. Wire key: `chunk_size_bytes`. */
    val chunkSizeBytes: Int = DEFAULT_CHUNK_SIZE_BYTES,

    /** Total number of chunks. Equal to ceil(totalBytes / chunkSizeBytes). Wire key: `chunk_count`. */
    val chunkCount: Int = 0,

    /** SHA-256 of each chunk's bytes, in chunk-index order. Hex-encoded lowercase. Wire key: `chunk_hashes`. */
    val chunkHashes: List<String> = emptyList(),

    /** Caller-defined MIME type or media kind. Opaque to the protocol. Wire key: `content_type`. */
    val contentType: String = "application/octet-stream",

    /** UTC creation time of the descriptor, ISO-8601 string for cross-language stability. Wire key: `created_at`. */
    val createdAt: String = nowIsoUtc(),
) {
    /**
     * Canonical wire JSON. Field order is fixed (see class doc) so output is
     * byte-identical across languages. Used directly and nested inside
     * [NamePublishPayload].
     */
    fun toJson(): String = buildString { appendJsonTo(this) }

    /** Append this descriptor's canonical JSON object onto [sb] (no surrounding whitespace). */
    fun appendJsonTo(sb: StringBuilder) {
        sb.append("{\"root_hash\":"); sb.appendJsonString(rootHash)
        sb.append(",\"name\":"); sb.appendJsonString(name)
        sb.append(",\"total_bytes\":").append(totalBytes)
        sb.append(",\"chunk_size_bytes\":").append(chunkSizeBytes)
        sb.append(",\"chunk_count\":").append(chunkCount)
        sb.append(",\"chunk_hashes\":[")
        for (i in chunkHashes.indices) {
            if (i > 0) sb.append(',')
            sb.appendJsonString(chunkHashes[i])
        }
        sb.append("],\"content_type\":"); sb.appendJsonString(contentType)
        sb.append(",\"created_at\":"); sb.appendJsonString(createdAt)
        sb.append('}')
    }

    companion object {
        const val DEFAULT_CHUNK_SIZE_BYTES = 262144

        /** Parse a descriptor from its canonical JSON string. Returns null on malformed input. */
        fun fromJson(json: String): ContentDescriptor? = try {
            fromJsonObject(JSONObject(json))
        } catch (_: Exception) {
            null
        }

        /** Parse a descriptor from an already-parsed [JSONObject] (used for nested decode). */
        fun fromJsonObject(o: JSONObject): ContentDescriptor {
            val hashesArr: JSONArray? = o.optJSONArray("chunk_hashes")
            val hashes = if (hashesArr == null) emptyList() else
                ArrayList<String>(hashesArr.length()).apply {
                    for (i in 0 until hashesArr.length()) add(hashesArr.getString(i))
                }
            return ContentDescriptor(
                rootHash       = o.optString("root_hash", ""),
                name           = o.optString("name", ""),
                totalBytes     = o.optLong("total_bytes", 0L),
                chunkSizeBytes = o.optInt("chunk_size_bytes", DEFAULT_CHUNK_SIZE_BYTES),
                chunkCount     = o.optInt("chunk_count", 0),
                chunkHashes    = hashes,
                contentType    = o.optString("content_type", "application/octet-stream"),
                createdAt      = o.optString("created_at", ""),
            )
        }

        /**
         * Build a descriptor from a buffer. Splits into [chunkSizeBytes]-sized chunks
         * (except the trailing chunk, which may be smaller), hashes each, and computes
         * the root over the chunk-hash concatenation.
         */
        fun fromBytes(
            name: String,
            data: ByteArray,
            contentType: String = "application/octet-stream",
            chunkSizeBytes: Int = DEFAULT_CHUNK_SIZE_BYTES,
        ): ContentDescriptor {
            val size = if (chunkSizeBytes <= 0) DEFAULT_CHUNK_SIZE_BYTES else chunkSizeBytes
            val chunkCount = ((data.size + size - 1) / size)
            val hashes = ArrayList<String>(chunkCount)
            val concat = ByteArray(chunkCount * 32)
            val sha = MessageDigest.getInstance("SHA-256")

            for (i in 0 until chunkCount) {
                val start = i * size
                val end = minOf(start + size, data.size)
                sha.reset()
                val h = sha.digest(data.copyOfRange(start, end))
                hashes += h.toHexLower()
                System.arraycopy(h, 0, concat, i * 32, 32)
            }

            sha.reset()
            val root = sha.digest(concat)

            return ContentDescriptor(
                rootHash = root.toHexLower(),
                name = name,
                totalBytes = data.size.toLong(),
                chunkSizeBytes = size,
                chunkCount = chunkCount,
                chunkHashes = hashes,
                contentType = contentType,
            )
        }

        private fun ByteArray.toHexLower(): String {
            val hex = "0123456789abcdef".toCharArray()
            val out = CharArray(size * 2)
            for (i in indices) {
                val v = this[i].toInt() and 0xff
                out[i * 2] = hex[v ushr 4]
                out[i * 2 + 1] = hex[v and 0x0f]
            }
            return String(out)
        }

        private fun nowIsoUtc(): String =
            java.time.Instant.now().toString()
    }

    /** Verify a chunk by recomputing its SHA-256 and comparing to [chunkHashes] at [chunkIndex]. */
    fun verifyChunk(chunkIndex: Int, chunkBytes: ByteArray): Boolean {
        if (chunkIndex < 0 || chunkIndex >= chunkHashes.size) return false
        val sha = MessageDigest.getInstance("SHA-256")
        val h = sha.digest(chunkBytes)
        return h.toHexLowerLocal() == chunkHashes[chunkIndex]
    }

    /** Recompute the root hash over [chunkHashes] and compare. Detects manifest tampering. */
    fun verifySelf(): Boolean {
        if (chunkHashes.size != chunkCount) return false
        val concat = ByteArray(chunkHashes.size * 32)
        for ((i, h) in chunkHashes.withIndex()) {
            val bytes = hexToBytes(h) ?: return false
            if (bytes.size != 32) return false
            System.arraycopy(bytes, 0, concat, i * 32, 32)
        }
        val sha = MessageDigest.getInstance("SHA-256")
        return sha.digest(concat).toHexLowerLocal() == rootHash
    }

    private fun ByteArray.toHexLowerLocal(): String {
        val hex = "0123456789abcdef".toCharArray()
        val out = CharArray(size * 2)
        for (i in indices) {
            val v = this[i].toInt() and 0xff
            out[i * 2] = hex[v ushr 4]
            out[i * 2 + 1] = hex[v and 0x0f]
        }
        return String(out)
    }

    private fun hexToBytes(s: String): ByteArray? {
        if (s.length % 2 != 0) return null
        val out = ByteArray(s.length / 2)
        for (i in out.indices) {
            val hi = Character.digit(s[i * 2], 16)
            val lo = Character.digit(s[i * 2 + 1], 16)
            if (hi < 0 || lo < 0) return null
            out[i] = ((hi shl 4) or lo).toByte()
        }
        return out
    }
}

/**
 * Append [s] as a JSON string literal (with surrounding quotes) onto the receiver,
 * escaping per RFC 8259. Shared by the hand-rolled wire encoders in the
 * `aethernet.content` package (ContentDescriptor, NamePublishPayload,
 * NameQueryPayload) so their output stays byte-identical to the C# reference.
 */
internal fun StringBuilder.appendJsonString(s: String) {
    append('"')
    for (c in s) {
        when (c) {
            '"'      -> append("\\\"")
            '\\'     -> append("\\\\")
            '\n'     -> append("\\n")
            '\r'     -> append("\\r")
            '\t'     -> append("\\t")
            '\b'     -> append("\\b")
            else     -> if (c < ' ') append("\\u").append(c.code.toString(16).padStart(4, '0')) else append(c)
        }
    }
    append('"')
}
