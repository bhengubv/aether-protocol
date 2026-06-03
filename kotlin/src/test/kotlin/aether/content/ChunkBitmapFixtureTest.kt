// SPDX-License-Identifier: MIT
package aether.content

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.DynamicTest.dynamicTest
import org.junit.jupiter.api.TestFactory
import java.io.File
import java.nio.file.Paths
import java.util.Base64

/**
 * Cross-language ChunkBitmap wire-format fixture verifier — Kotlin runner.
 *
 * Reads fixtures/content/chunk_bitmap_vectors.json and verifies that this
 * implementation produces bit-identical bitsets and JSON payloads for each
 * pinned test vector.
 */

// ── Model ──────────────────────────────────────────────────────────────────────

@Serializable
private data class ChunkBitmapVector(
    val name: String,
    val description: String,
    @SerialName("root_hash")         val rootHash: String,
    @SerialName("chunk_count")       val chunkCount: Int,
    @SerialName("have_indices")      val haveIndices: List<Int>,
    @SerialName("have_bitset_hex")   val haveBitsetHex: String,
    @SerialName("have_bitset_base64") val haveBitsetBase64: String,
    val generation: Long,
    @SerialName("expected_json")     val expectedJson: String,
)

class ChunkBitmapFixtureTest {

    // ── Inline BitsetCodec (spec compliance — does not import from main src) ───

    private fun inlineEncode(chunkCount: Int, indices: List<Int>): ByteArray {
        if (chunkCount <= 0) return ByteArray(0)
        val bytes = ByteArray((chunkCount + 7) / 8)
        for (i in indices) {
            bytes[i shr 3] = (bytes[i shr 3].toInt() or (1 shl (i and 7))).toByte()
        }
        return bytes
    }

    private fun inlineDecode(bitset: ByteArray, chunkCount: Int): List<Int> {
        val result = mutableListOf<Int>()
        val limit = minOf(chunkCount, bitset.size * 8)
        for (i in 0 until limit) {
            if ((bitset[i shr 3].toInt() and 0xFF) and (1 shl (i and 7)) != 0) result.add(i)
        }
        return result
    }

    private fun inlineMarshal(
        rootHash: String,
        chunkCount: Int,
        haveBitset: ByteArray,
        generation: Long,
    ): String {
        val b64 = Base64.getEncoder().encodeToString(haveBitset)
        return """{"root_hash":"$rootHash","chunk_count":$chunkCount,"have_bitset":"$b64","generation":$generation}"""
    }

    // ── Fixture loader ─────────────────────────────────────────────────────────

    private val jsonParser = Json { ignoreUnknownKeys = true }

    private fun loadVectors(): List<ChunkBitmapVector> {
        var dir = File(Paths.get("").toAbsolutePath().toString())
        repeat(12) {
            val candidate = File(dir, "fixtures/content/chunk_bitmap_vectors.json")
            if (candidate.exists()) {
                return jsonParser.decodeFromString<List<ChunkBitmapVector>>(candidate.readText())
            }
            dir = dir.parentFile ?: return@repeat
        }
        throw IllegalStateException("Could not locate fixtures/content/chunk_bitmap_vectors.json")
    }

    private val vectors by lazy { loadVectors() }

    // ── Tests ──────────────────────────────────────────────────────────────────

    @TestFactory
    fun `encode produces correct bitset`(): List<DynamicTest> = vectors.map { v ->
        dynamicTest(v.name) {
            val bitset = inlineEncode(v.chunkCount, v.haveIndices)
            val hex = bitset.joinToString("") { "%02x".format(it) }
            assertEquals(v.haveBitsetHex.lowercase(), hex)
            assertEquals(v.haveBitsetBase64, Base64.getEncoder().encodeToString(bitset))
        }
    }

    @TestFactory
    fun `decode recovers correct indices`(): List<DynamicTest> = vectors.map { v ->
        dynamicTest(v.name) {
            val bitset = Base64.getDecoder().decode(v.haveBitsetBase64)
            val recovered = inlineDecode(bitset, v.chunkCount)
            assertEquals(v.haveIndices.sorted(), recovered.sorted())
        }
    }

    @TestFactory
    fun `JSON serialization matches expected`(): List<DynamicTest> = vectors.map { v ->
        dynamicTest(v.name) {
            val bitset = inlineEncode(v.chunkCount, v.haveIndices)
            val actual = inlineMarshal(v.rootHash, v.chunkCount, bitset, v.generation)
            assertEquals(v.expectedJson, actual)
        }
    }

    @TestFactory
    fun `bitset length is ceil div 8`(): List<DynamicTest> = vectors.map { v ->
        dynamicTest(v.name) {
            val bitset = inlineEncode(v.chunkCount, v.haveIndices)
            assertEquals((v.chunkCount + 7) / 8, bitset.size)
        }
    }

    @TestFactory
    fun `trailing bits are zero`(): List<DynamicTest> = vectors.map { v ->
        dynamicTest(v.name) {
            val bitset = inlineEncode(v.chunkCount, v.haveIndices)
            if (bitset.isEmpty()) return@dynamicTest
            val trailing = v.chunkCount % 8
            if (trailing == 0) return@dynamicTest
            val last = bitset.last().toInt() and 0xFF
            val validMask = (1 shl trailing) - 1
            assertEquals(0, last and validMask.inv().and(0xFF))
        }
    }
}
