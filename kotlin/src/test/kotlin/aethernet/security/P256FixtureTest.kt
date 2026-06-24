// SPDX-License-Identifier: MIT

package aethernet.security

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.DynamicTest.dynamicTest
import org.junit.jupiter.api.TestFactory
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Drives [Ed25519Service.verifyWithFallback] through the shared P-256 corpus that every
 * AetherNet SDK consumes (`tests/cross-language/p256-fixtures.json`): a DER
 * SubjectPublicKeyInfo public key + an ASN.1 DER ECDSA signature over SHA-256, per
 * PROTOCOL_SPEC.md §7.5. An Ed25519-only regression would reject the valid vector and
 * fail here, so the legacy fallback can never silently drop back to a stub.
 *
 * Corpus location: `tests/cross-language/p256-fixtures.json` at the repo root.
 */
class P256FixtureTest {

    private val corpus: JsonObject by lazy { loadCorpus() }

    private fun loadCorpus(): JsonObject {
        // CWD is kotlin/ when Gradle runs tests; the corpus is two levels up.
        // Walk up to handle deeper test runners (IDE, classpath jar, etc.).
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "tests/cross-language/p256-fixtures.json")
            if (candidate.exists()) {
                return Json.parseToJsonElement(candidate.readText()).jsonObject
            }
            dir = dir?.parentFile ?: return@repeat
        }
        error(
            "Could not locate tests/cross-language/p256-fixtures.json walking up " +
                "from ${File(".").canonicalPath}"
        )
    }

    @TestFactory
    fun `verifyWithFallback matches every P-256 vector`(): List<DynamicTest> =
        corpus["vectors"]!!.jsonArray.map { fixture ->
            val obj = fixture.jsonObject
            val name = obj.string("name")
            dynamicTest("p256: $name") {
                val pub = hexToBytes(obj.string("public_key_der"))
                val msg = hexToBytes(obj.string("message"))
                val sig = hexToBytes(obj.string("signature_der"))
                val expected = obj["valid"]!!.jsonPrimitive.boolean

                // A >32-byte key forces the P-256 branch; the Ed25519 path only takes
                // exactly-32-byte keys.
                assertTrue(pub.size > 32, "$name: P-256 key must be > 32 bytes")
                assertEquals(
                    expected,
                    Ed25519Service.verifyWithFallback(pub, msg, sig),
                    name,
                )
            }
        }

    private fun JsonObject.string(key: String): String =
        this[key]!!.jsonPrimitive.content

    private fun hexToBytes(hex: String): ByteArray =
        ByteArray(hex.length / 2) { i ->
            hex.substring(i * 2, i * 2 + 2).toInt(16).toByte()
        }
}
