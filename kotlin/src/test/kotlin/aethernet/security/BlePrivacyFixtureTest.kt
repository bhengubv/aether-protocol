// SPDX-License-Identifier: MIT
package aethernet.security

import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Cross-language BLE-privacy fixture verifier.
 *
 * Verifies that the Kotlin implementation reproduces fixtures/bleprivacy/vectors.json
 * byte-for-byte — the same rotating Service UUID and IRK-based Resolvable Private
 * Address (RPA) outputs that the C# reference and every AetherNet language SDK must
 * produce. Any drift surfaces here as a UUID / hex mismatch.
 */
class BlePrivacyFixtureTest {

    // ─── Fixture parity gate ────────────────────────────────────────────────

    @Test
    fun `bleprivacy uuid vectors match byte-for-byte`() {
        val fx = loadFixture()
        assertTrue(fx.uuidVectors.isNotEmpty(), "no uuid_vectors in fixture")
        for (v in fx.uuidVectors) {
            assertEquals(
                v.text,
                BlePrivacy.serviceUuid(fx.rotationKey, v.window),
                "serviceUuid mismatch for window=${v.window}",
            )
        }
    }

    @Test
    fun `bleprivacy rpa vectors resolve and reject byte-for-byte`() {
        val fx = loadFixture()
        assertTrue(fx.rpaVectors.isNotEmpty(), "no rpa_vectors in fixture")
        for (v in fx.rpaVectors) {
            val rpa = BlePrivacy.resolvableAddress(fx.irk, v.window)
            assertEquals(v.text, hex(rpa), "resolvableAddress mismatch for window=${v.window}")

            assertTrue(
                BlePrivacy.resolveAddress(fx.irk, rpa),
                "correct IRK must resolve its own RPA (window=${v.window})",
            )
            assertFalse(
                BlePrivacy.resolveAddress(fx.wrongIrk, rpa),
                "wrong IRK must not resolve the RPA (window=${v.window})",
            )
        }
    }

    @Test
    fun `bleprivacy rotation seconds matches fixture`() {
        val fx = loadFixture()
        assertEquals(fx.rotationSeconds, BlePrivacy.ROTATION_SECONDS)
    }

    // ─── windowFor boundary + reject paths ──────────────────────────────────

    @Test
    fun `windowFor floors to the rotation boundary`() {
        assertEquals(0L, BlePrivacy.windowFor(899))
        assertEquals(1L, BlePrivacy.windowFor(900))
    }

    @Test
    fun `resolvableAddress rejects a 15-byte IRK`() {
        assertThrows<IllegalArgumentException> {
            BlePrivacy.resolvableAddress(ByteArray(15), 0L)
        }
    }

    // ─── Fixture model + loader (mirrors Bip39FixtureTest / SignalFixtureTest) ─

    private data class Vector(val window: Long, val text: String)

    private data class Fixture(
        val rotationSeconds: Int,
        val rotationKey: ByteArray,
        val irk: ByteArray,
        val wrongIrk: ByteArray,
        val uuidVectors: List<Vector>,
        val rpaVectors: List<Vector>,
    )

    private fun loadFixture(): Fixture {
        val json = File(repoRoot(), "fixtures/bleprivacy/vectors.json").readText()
        return Fixture(
            rotationSeconds = scalarInt(json, "rotation_seconds"),
            rotationKey = unhex(scalarString(json, "rotation_key")),
            irk = unhex(scalarString(json, "irk")),
            wrongIrk = unhex(scalarString(json, "wrong_irk")),
            uuidVectors = parseVectors(json, "uuid_vectors", "uuid"),
            rpaVectors = parseVectors(json, "rpa_vectors", "rpa"),
        )
    }

    private fun repoRoot(): File {
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "AetherNetProtocol.slnx")
            if (candidate.exists()) return dir!!
            dir = dir?.parentFile ?: return@repeat
        }
        throw IllegalStateException("AetherNetProtocol.slnx not found from ${File(".").canonicalFile}")
    }

    private fun scalarString(json: String, name: String): String =
        Regex("\"$name\"\\s*:\\s*\"([^\"]*)\"").find(json)!!.groupValues[1]

    private fun scalarInt(json: String, name: String): Int =
        Regex("\"$name\"\\s*:\\s*(-?\\d+)").find(json)!!.groupValues[1].toInt()

    /**
     * Parses a named array of `{ "window": <int>, "<textKey>": "<string>" }`
     * objects. Same bracket-scanning approach as the sibling fixture tests, but
     * pulls the numeric `window` alongside the string field per object.
     */
    private fun parseVectors(json: String, arrayName: String, textKey: String): List<Vector> {
        val nameIdx = json.indexOf("\"$arrayName\"")
        require(nameIdx >= 0) { "No '$arrayName' array in vectors.json" }
        val openBracket = json.indexOf('[', nameIdx)
        var depth = 0
        var endIdx = -1
        for (i in openBracket until json.length) {
            when (json[i]) {
                '[' -> depth++
                ']' -> { depth--; if (depth == 0) { endIdx = i; break } }
            }
        }
        require(endIdx > 0) { "Unbalanced '$arrayName' array" }
        val arrayBody = json.substring(openBracket + 1, endIdx)
        return splitObjects(arrayBody).map { obj ->
            val window = Regex("\"window\"\\s*:\\s*(-?\\d+)").find(obj)!!.groupValues[1].toLong()
            val text = Regex("\"$textKey\"\\s*:\\s*\"([^\"]*)\"").find(obj)!!.groupValues[1]
            Vector(window, text)
        }
    }

    private fun splitObjects(text: String): List<String> {
        val result = mutableListOf<String>()
        var depth = 0
        var start = -1
        for (i in text.indices) {
            when (text[i]) {
                '{' -> { if (depth == 0) start = i; depth++ }
                '}' -> { depth--; if (depth == 0 && start >= 0) { result += text.substring(start, i + 1); start = -1 } }
            }
        }
        return result
    }

    private fun unhex(s: String): ByteArray {
        require(s.length % 2 == 0) { "hex string must have even length" }
        return ByteArray(s.length / 2) { i ->
            ((Character.digit(s[i * 2], 16) shl 4) + Character.digit(s[i * 2 + 1], 16)).toByte()
        }
    }

    private fun hex(b: ByteArray): String =
        b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
}
