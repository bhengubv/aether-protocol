// SPDX-License-Identifier: MIT

package aethernet.uri

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.DynamicTest.dynamicTest
import org.junit.jupiter.api.TestFactory
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

/**
 * Drives the Kotlin implementation through the same JSON corpus that every
 * other AetherNet SDK consumes. If a fixture passes here and fails in another
 * language port, the port is wrong — not the corpus.
 *
 * Corpus location: `tests/cross-language/uri-fixtures.json` at the repo root.
 */
class AetherUriCrossLanguageFixtureTest {

    private val corpus: JsonObject by lazy { loadCorpus() }

    private fun loadCorpus(): JsonObject {
        // CWD is kotlin/ when Gradle runs tests; the corpus is two levels up.
        // Walk up to handle deeper test runners (IDE, classpath jar, etc.).
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "tests/cross-language/uri-fixtures.json")
            if (candidate.exists()) {
                return Json.parseToJsonElement(candidate.readText()).jsonObject
            }
            dir = dir?.parentFile ?: return@repeat
        }
        error(
            "Could not locate tests/cross-language/uri-fixtures.json walking up " +
                "from ${File(".").canonicalPath}"
        )
    }

    // ── Valid corpus ────────────────────────────────────────────────────────

    @TestFactory
    fun `valid fixture parses to expected components`(): List<DynamicTest> =
        corpus["valid"]!!.jsonArray.map { fixture ->
            val obj = fixture.jsonObject
            val name = obj.string("name")
            dynamicTest("valid: $name") {
                val input = obj.string("input")
                val canonical = obj.string("canonical")
                val u = AetherUri.parse(input)
                assertEquals(canonical, u.toString(), "canonical mismatch for $name")
                assertEquals(obj.string("authority"), u.authority, "authority for $name")
                assertEquals(obj.string("path"), u.path, "path for $name")
                assertEquals(obj.string("handlerName"), u.handlerName, "handlerName for $name")
                assertEquals(obj.string("fragment"), u.fragment, "fragment for $name")

                val expectedQuery = obj["query"]!!.jsonObject
                    .mapValues { (_, v) -> v.jsonPrimitive.content }
                assertEquals(
                    expectedQuery.size, u.query.size,
                    "query size for $name (got=${u.query})"
                )
                for ((k, v) in expectedQuery) {
                    assertEquals(v, u.query[k], "query[$k] for $name")
                }

                val expectedSegs = obj["pathSegments"]!!.jsonArray
                    .map { it.jsonPrimitive.content }
                assertEquals(expectedSegs, u.pathSegments, "pathSegments for $name")
            }
        }

    // ── Invalid corpus ──────────────────────────────────────────────────────

    @TestFactory
    fun `invalid fixture fails to parse`(): List<DynamicTest> =
        corpus["invalid"]!!.jsonArray.map { fixture ->
            val obj = fixture.jsonObject
            val name = obj.string("name")
            dynamicTest("invalid: $name") {
                val input = obj.string("input")
                val result = AetherUri.tryParse(input)
                assertTrue(
                    result is AetherUri.ParseResult.Err,
                    "expected $name to fail to parse but it succeeded"
                )
            }
        }

    // ── Manifest corpus ─────────────────────────────────────────────────────

    @TestFactory
    fun `manifest fixture resolves as expected`(): List<DynamicTest> {
        val manifestDef = corpus["manifest"]!!.jsonObject
        val appId = manifestDef.string("appId")
        val handlers = manifestDef["handlers"]!!.jsonArray.map { h ->
            HandlerDescriptor(
                name = h.jsonObject.string("handlerName"),
                pathTemplate = h.jsonObject.string("pathTemplate"),
            )
        }
        val manifest = HandlerManifest(appId, handlers)

        return manifestDef["matches"]!!.jsonArray.map { fixture ->
            val obj = fixture.jsonObject
            val input = obj.string("input")
            dynamicTest("manifest: $input") {
                val u = AetherUri.parse(input)
                val resolved = manifest.resolve(u)
                val expectedMatched = obj["matched"]!!.jsonPrimitive.boolean

                if (!expectedMatched) {
                    assertNull(resolved, "expected $input to NOT match")
                    return@dynamicTest
                }

                assertNotNull(resolved, "expected $input to match")
                val expectedIndex = obj["handlerIndex"]!!.jsonPrimitive.int
                assertSame(
                    handlers[expectedIndex], resolved.first,
                    "wrong handler for $input"
                )
                val expectedCaps = obj["captures"]!!.jsonObject
                    .mapValues { (_, v) -> v.jsonPrimitive.content }
                assertEquals(
                    expectedCaps.size, resolved.second.size,
                    "capture count for $input (got=${resolved.second})"
                )
                for ((k, v) in expectedCaps) {
                    assertEquals(v, resolved.second[k], "capture[$k] for $input")
                }
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private fun JsonObject.string(key: String): String =
        this[key]!!.jsonPrimitive.content
}
