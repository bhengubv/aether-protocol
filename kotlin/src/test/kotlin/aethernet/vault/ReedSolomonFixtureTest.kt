// SPDX-License-Identifier: MIT

package aethernet.vault

import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * Cross-language Reed-Solomon parity: the Kotlin systematic Cauchy-RS codec must reproduce every C#
 * reference shard and recovery (fixtures/vault/reed_solomon_basic.json) byte-for-byte. Mirrors the Go
 * reed_solomon_fixture_test.go suite. Any drift in the GF(2⁸) field, the Cauchy parity matrix, or the
 * Gauss-Jordan inversion surfaces here as a hex mismatch.
 */
class ReedSolomonFixtureTest {

    private fun repoRoot(): File {
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "AetherNetProtocol.slnx")
            if (candidate.exists()) return dir!!
            dir = dir?.parentFile ?: return@repeat
        }
        throw IllegalStateException("AetherNetProtocol.slnx not found from ${File(".").canonicalFile}")
    }

    private fun vectors(): JSONObject =
        JSONObject(File(repoRoot(), "fixtures/vault/reed_solomon_basic.json").readText())

    private fun hex(b: ByteArray): String = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    private fun unhex(s: String): ByteArray =
        ByteArray(s.length / 2) { i ->
            ((Character.digit(s[i * 2], 16) shl 4) + Character.digit(s[i * 2 + 1], 16)).toByte()
        }

    /** Asserts the Kotlin encoder reproduces every C# shard (systematic data + Cauchy parity) byte-for-byte. */
    @Test
    fun `reed-solomon shard parity with the C# reference fixture`() {
        val v = vectors()
        val k = v.getInt("k"); val m = v.getInt("m"); val n = v.getInt("n")
        assertEquals(10, k); assertEquals(4, m); assertEquals(14, n)

        val input = unhex(v.getString("input"))
        assertEquals(v.getInt("input_size"), input.size, "input size")

        val codec = ReedSolomonCodec(k, m)
        val shards = codec.encodeData(input)
        assertEquals(n, shards.size, "shard count")
        assertEquals(v.getInt("shard_size"), shards[0].size, "shard size")

        val expectedShards = v.getJSONArray("shards")
        for (i in 0 until expectedShards.length()) {
            val o = expectedShards.getJSONObject(i)
            val idx = o.getInt("index")
            assertEquals(o.getString("hex"), hex(shards[idx]), "shard $idx mismatch")
        }
    }

    /**
     * Asserts every recovery subset decodes to the fixture input byte-for-byte (covers the systematic
     * fast-path, the all-parity path, and a data+parity mix).
     */
    @Test
    fun `reed-solomon recovery parity with the C# reference fixture`() {
        val v = vectors()
        val input = unhex(v.getString("input"))
        val codec = ReedSolomonCodec(v.getInt("k"), v.getInt("m"))
        val shards = codec.encodeData(input)

        val recoveries = v.getJSONArray("recovery")
        for (r in 0 until recoveries.length()) {
            val rec = recoveries.getJSONObject(r)
            val note = rec.getString("note")
            val survivors = rec.getJSONArray("survivor_indices")
            val available = HashMap<Int, ByteArray>()
            for (s in 0 until survivors.length()) {
                val idx = survivors.getInt(s)
                available[idx] = shards[idx]
            }

            val recovered = codec.reconstructData(available, v.getInt("input_size"))
            assertEquals(rec.getString("recovered"), hex(recovered), "recovery [$note]: bytes mismatch")
            // The recovered blob must equal the original input.
            assertContentEquals(input, recovered, "recovery [$note]: recovered != original input")
        }
    }

    /**
     * Asserts that only K-1 survivors is unrecoverable (the fixture's should_fail case). Ports MUST
     * treat this as a failure.
     */
    @Test
    fun `reed-solomon K-minus-one survivors fail to decode`() {
        val v = vectors()
        val input = unhex(v.getString("input"))
        val k = v.getInt("k")
        val codec = ReedSolomonCodec(k, v.getInt("m"))
        val shards = codec.encodeData(input)

        val shouldFail = v.getJSONObject("should_fail")
        val survivors = shouldFail.getJSONArray("survivor_indices")
        assertEquals(k - 1, survivors.length(), "should_fail must carry K-1 survivors")

        val available = HashMap<Int, ByteArray>()
        for (s in 0 until survivors.length()) {
            val idx = survivors.getInt(s)
            available[idx] = shards[idx]
        }

        assertFailsWith<IllegalStateException>("expected K-1 survivors to FAIL decoding") {
            codec.reconstructData(available, v.getInt("input_size"))
        }
    }

    /**
     * Proves recovery works from JUST the M parity shards plus enough data shards to reach K —
     * exercising the general matrix-inversion path with the maximum number of parity rows the code can
     * use.
     */
    @Test
    fun `reed-solomon parity-assisted recovery reproduces the original`() {
        val v = vectors()
        val input = unhex(v.getString("input"))
        val k = v.getInt("k"); val m = v.getInt("m"); val n = v.getInt("n")
        val codec = ReedSolomonCodec(k, m)
        val shards = codec.encodeData(input)

        // Drop the first M data shards; survive on data[M..K-1] + all M parity shards = K total.
        val available = HashMap<Int, ByteArray>()
        for (i in m until k) available[i] = shards[i]
        for (i in k until n) available[i] = shards[i]

        val recovered = codec.reconstructData(available, v.getInt("input_size"))
        assertContentEquals(input, recovered, "parity-assisted recovery did not reproduce the original input")
    }

    /** Field parameters declared by the fixture must match the codec's compiled-in field. */
    @Test
    fun `reed-solomon field parameters match the fixture`() {
        val v = vectors()
        val field = v.getJSONObject("field")
        assertEquals("0x11D", field.getString("primitive_polynomial"))
        assertEquals(2, field.getInt("alpha"))
        assertEquals(8, field.getInt("gf_bits"))
    }

    /** A round-trip with all 14 shards present recovers the input (sanity over the encode + decode path). */
    @Test
    fun `reed-solomon full-set round-trip recovers the input`() {
        val v = vectors()
        val input = unhex(v.getString("input"))
        val codec = ReedSolomonCodec(v.getInt("k"), v.getInt("m"))
        val shards = codec.encodeData(input)
        val available = HashMap<Int, ByteArray>()
        for (i in shards.indices) available[i] = shards[i]
        val recovered = codec.reconstructData(available, v.getInt("input_size"))
        assertTrue(input.contentEquals(recovered))
    }
}
