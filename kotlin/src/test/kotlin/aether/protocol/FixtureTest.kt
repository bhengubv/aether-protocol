// SPDX-License-Identifier: MIT
package aether.protocol

import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.TestFactory
import java.io.File
import java.util.UUID
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals

/**
 * Cross-language wire-format fixture verifier. Reads
 * `../../../fixtures/inputs.json` and `../../../fixtures/expected/<name>.bin` files and
 * asserts that this language's PacketSerializer produces byte-identical output
 * for each canonical input. See `fixtures/README.md`.
 */
class FixtureTest {

    private data class FixtureInput(
        val name: String,
        val id: String,
        val type: Int,
        val sourceUhid: String,
        val destinationUhid: String,
        val ttl: Int,
        val priority: Int,
        val payloadHex: String,
        val packetNonceHex: String,
        val signatureHex: String,
        val timestampMs: Long,
        val protocolVersion: Int,
    )

    private fun fixturesDir(): File {
        // CWD is kotlin/ when Gradle runs tests; the repo is one up.
        var dir: File? = File(".").canonicalFile
        repeat(8) {
            val candidate = File(dir, "fixtures/inputs.json")
            if (candidate.exists()) return File(dir, "fixtures")
            dir = dir?.parentFile ?: return@repeat
        }
        error("Could not locate fixtures/inputs.json from ${File(".").canonicalPath}")
    }

    private fun hex(s: String): ByteArray {
        if (s.isEmpty()) return ByteArray(0)
        val out = ByteArray(s.length / 2)
        for (i in out.indices) {
            out[i] = s.substring(i * 2, i * 2 + 2).toInt(16).toByte()
        }
        return out
    }

    /**
     * Tiny hand-rolled JSON-array reader. Avoids pulling in a JSON dep just for
     * the fixture tests; the schema is a flat array of flat objects.
     */
    private fun loadInputs(): List<FixtureInput> {
        val text = File(fixturesDir(), "inputs.json").readText()
        val items = mutableListOf<FixtureInput>()
        var i = text.indexOf('[')
        require(i >= 0) { "expected top-level array" }
        i++
        while (i < text.length) {
            // Skip whitespace and commas
            while (i < text.length && (text[i].isWhitespace() || text[i] == ',')) i++
            if (i >= text.length || text[i] == ']') break
            // Read one object
            require(text[i] == '{') { "expected '{' at $i" }
            var depth = 1
            val start = i
            i++
            while (i < text.length && depth > 0) {
                when (text[i]) {
                    '{' -> depth++
                    '}' -> depth--
                    '"' -> { // skip quoted string with escapes
                        i++
                        while (i < text.length && text[i] != '"') {
                            if (text[i] == '\\') i++
                            i++
                        }
                    }
                }
                i++
            }
            val obj = text.substring(start, i)
            items.add(parseObject(obj))
        }
        return items
    }

    private fun parseObject(obj: String): FixtureInput {
        fun strField(key: String): String {
            val needle = "\"$key\":"
            var p = obj.indexOf(needle) + needle.length
            while (p < obj.length && obj[p].isWhitespace()) p++
            require(obj[p] == '"') { "expected string for $key" }
            p++
            val sb = StringBuilder()
            while (p < obj.length && obj[p] != '"') {
                if (obj[p] == '\\' && p + 1 < obj.length) {
                    when (obj[p + 1]) {
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        '"' -> sb.append('"')
                        '\\' -> sb.append('\\')
                        else -> sb.append(obj[p + 1])
                    }
                    p += 2
                } else {
                    sb.append(obj[p]); p++
                }
            }
            return sb.toString()
        }
        fun longField(key: String): Long {
            val needle = "\"$key\":"
            var p = obj.indexOf(needle) + needle.length
            while (p < obj.length && obj[p].isWhitespace()) p++
            val start = p
            while (p < obj.length && (obj[p].isDigit() || obj[p] == '-')) p++
            return obj.substring(start, p).toLong()
        }
        return FixtureInput(
            name = strField("name"),
            id = strField("id"),
            type = longField("type").toInt(),
            sourceUhid = strField("source_uhid"),
            destinationUhid = strField("destination_uhid"),
            ttl = longField("ttl").toInt(),
            priority = longField("priority").toInt(),
            payloadHex = strField("payload_hex"),
            packetNonceHex = strField("packet_nonce_hex"),
            signatureHex = strField("signature_hex"),
            timestampMs = longField("timestamp_ms"),
            protocolVersion = longField("protocol_version").toInt(),
        )
    }

    private fun packetFrom(input: FixtureInput): MeshPacket =
        MeshPacket(
            id = UUID.fromString(input.id),
            type = PacketType.fromValue(input.type.toByte())
                ?: error("unknown packet type ${input.type}"),
            sourceUhid = input.sourceUhid,
            destinationUhid = input.destinationUhid,
            ttl = input.ttl,
            priority = input.priority.toByte(),
            payload = hex(input.payloadHex),
            packetNonce = hex(input.packetNonceHex),
            signature = hex(input.signatureHex),
            timestampMs = input.timestampMs,
            protocolVersion = input.protocolVersion.toByte(),
        )

    @TestFactory
    fun fixturesSerializeToExpectedBytes(): List<DynamicTest> =
        loadInputs().map { input ->
            DynamicTest.dynamicTest("serialize ${input.name} → expected bytes") {
                val got = PacketSerializer.serialize(packetFrom(input))
                val expected = File(fixturesDir(), "expected/${input.name}.bin").readBytes()
                assertContentEquals(expected, got, "${input.name}: see fixtures/README.md")
            }
        }

    @TestFactory
    fun fixturesDeserializeFromExpectedBytes(): List<DynamicTest> =
        loadInputs().map { input ->
            DynamicTest.dynamicTest("deserialize ${input.name} → input fields") {
                val expected = File(fixturesDir(), "expected/${input.name}.bin").readBytes()
                val got = PacketSerializer.deserialize(expected)
                assertEquals(UUID.fromString(input.id), got.id)
                assertEquals(
                    PacketType.fromValue(input.type.toByte())!!,
                    got.type,
                )
                assertEquals(input.sourceUhid, got.sourceUhid)
                assertEquals(input.destinationUhid, got.destinationUhid)
                assertEquals(input.ttl, got.ttl)
                assertEquals(input.priority.toByte(), got.priority)
                assertContentEquals(hex(input.payloadHex), got.payload)
                assertContentEquals(hex(input.packetNonceHex), got.packetNonce)
                assertContentEquals(hex(input.signatureHex), got.signature)
                assertEquals(input.timestampMs, got.timestampMs)
                assertEquals(input.protocolVersion.toByte(), got.protocolVersion)
            }
        }
}
