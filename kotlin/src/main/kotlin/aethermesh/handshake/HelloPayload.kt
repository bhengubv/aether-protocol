// SPDX-License-Identifier: MIT

package aethermesh.handshake

/**
 * Wire payload carried inside a `PacketType.Hello` or `PacketType.HelloAck`
 * packet's `MeshPacket.Payload`.
 *
 * JSON shape (snake_case to match the rest of the Aether wire format):
 *
 * ```
 * {
 *   "min_version": 1,
 *   "max_version": 2,
 *   "capabilities": ["signal-x3dh", "double-ratchet", "dtn-custody"],
 *   "implementation": "aether-kotlin/1.0.0"
 * }
 * ```
 *
 * Notes on security: this payload is NEITHER encrypted NOR authenticated by
 * design — the handshake runs before any Signal session exists. Peer identity
 * is verified later via Ed25519 packet signatures on the data packets the
 * peer subsequently sends. Treat the announced capabilities as a hint, not
 * as a security claim.
 *
 * MUST stay byte-compatible with the C# / Go HelloPayload (same field names,
 * same snake_case JSON keys). Cross-language interop is verified by C# peers
 * deserialising Kotlin-emitted Hello packets and vice versa.
 */
data class HelloPayload(
    /** Lowest protocol version the announcer can speak. */
    val minVersion: Byte = 0,
    /** Highest protocol version the announcer can speak. */
    val maxVersion: Byte = 0,
    /**
     * Capability tags advertised by the announcer. Capability names are wire
     * constants — case-sensitive, not human strings.
     */
    val capabilities: List<String> = emptyList(),
    /**
     * Free-form implementation banner (e.g. `"aether-kotlin/1.0.0"`).
     * Diagnostic only; not used for compatibility decisions.
     */
    val implementation: String = "",
) {
    /**
     * Encodes this payload as UTF-8 JSON. Snake-case field names match the
     * cross-language wire shape exactly.
     */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"min_version\":").append(minVersion.toInt() and 0xFF).append(',')
        sb.append("\"max_version\":").append(maxVersion.toInt() and 0xFF).append(',')
        sb.append("\"capabilities\":[")
        for (i in capabilities.indices) {
            if (i > 0) sb.append(',')
            sb.append('"').append(jsonEscape(capabilities[i])).append('"')
        }
        sb.append(']').append(',')
        sb.append("\"implementation\":\"").append(jsonEscape(implementation)).append("\"")
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    companion object {
        /**
         * Decodes a UTF-8 JSON-encoded HelloPayload. Returns null if the bytes
         * cannot be parsed — caller logs and drops the packet (handshake is
         * unauthenticated, malformed payloads are discarded silently).
         *
         * Tolerant parser: accepts arbitrary key ordering, whitespace, and
         * unknown fields. Required fields default to zero / empty.
         */
        fun fromJsonBytesOrNull(bytes: ByteArray?): HelloPayload? {
            if (bytes == null || bytes.isEmpty()) return null
            return try {
                parse(String(bytes, Charsets.UTF_8))
            } catch (_: Exception) {
                null
            }
        }

        /**
         * Parses a HelloPayload from a JSON string. Throws on malformed input.
         * Internal — public callers use [fromJsonBytesOrNull] which never
         * throws.
         */
        internal fun parse(text: String): HelloPayload {
            val obj = HelloJsonReader(text).readObject()
            val minVersion = (obj["min_version"] as? Number)?.toInt()?.toByte() ?: 0
            val maxVersion = (obj["max_version"] as? Number)?.toInt()?.toByte() ?: 0
            @Suppress("UNCHECKED_CAST")
            val caps = (obj["capabilities"] as? List<Any?>)?.mapNotNull { it as? String } ?: emptyList()
            val impl = obj["implementation"] as? String ?: ""
            return HelloPayload(minVersion, maxVersion, caps, impl)
        }
    }
}

private fun jsonEscape(s: String): String {
    val sb = StringBuilder(s.length + 2)
    for (c in s) {
        when (c) {
            '\\' -> sb.append("\\\\")
            '"' -> sb.append("\\\"")
            '\b' -> sb.append("\\b")
            '\u000C' -> sb.append("\\f")
            '\n' -> sb.append("\\n")
            '\r' -> sb.append("\\r")
            '\t' -> sb.append("\\t")
            else -> {
                if (c.code < 0x20) {
                    sb.append("\\u").append("%04x".format(c.code))
                } else {
                    sb.append(c)
                }
            }
        }
    }
    return sb.toString()
}

/**
 * Minimal JSON reader for the fixed-shape HelloPayload. Tolerates whitespace,
 * arbitrary key ordering, unknown fields, and string / number / bool / null /
 * array / object values.
 *
 * Internal: not a general-purpose JSON parser. Sufficient for the single
 * payload type used here. Hosts wiring up arbitrary JSON should bring their
 * own library (kotlinx.serialization, Jackson, etc.).
 */
internal class HelloJsonReader(private val text: String) {
    private var pos: Int = 0

    fun readObject(): Map<String, Any?> {
        skipWs()
        require(peek() == '{') { "expected '{' at position $pos" }
        pos++
        val map = LinkedHashMap<String, Any?>()
        skipWs()
        if (peek() == '}') { pos++; return map }
        while (true) {
            skipWs()
            val key = readString()
            skipWs()
            require(peek() == ':') { "expected ':' at position $pos" }
            pos++
            skipWs()
            val value = readValue()
            map[key] = value
            skipWs()
            when (peek()) {
                ',' -> { pos++; continue }
                '}' -> { pos++; return map }
                else -> throw IllegalArgumentException("expected ',' or '}' at position $pos")
            }
        }
    }

    private fun readArray(): List<Any?> {
        require(peek() == '[') { "expected '[' at position $pos" }
        pos++
        val list = mutableListOf<Any?>()
        skipWs()
        if (peek() == ']') { pos++; return list }
        while (true) {
            skipWs()
            list += readValue()
            skipWs()
            when (peek()) {
                ',' -> { pos++; continue }
                ']' -> { pos++; return list }
                else -> throw IllegalArgumentException("expected ',' or ']' at position $pos")
            }
        }
    }

    private fun readValue(): Any? {
        skipWs()
        return when (peek()) {
            '"' -> readString()
            '{' -> readObject()
            '[' -> readArray()
            't', 'f' -> readBool()
            'n' -> readNull()
            else -> readNumber()
        }
    }

    private fun readString(): String {
        require(peek() == '"') { "expected '\"' at position $pos" }
        pos++
        val sb = StringBuilder()
        while (pos < text.length) {
            val c = text[pos]
            if (c == '"') { pos++; return sb.toString() }
            if (c == '\\') {
                pos++
                require(pos < text.length) { "unterminated escape at position $pos" }
                when (val esc = text[pos]) {
                    '"' -> sb.append('"')
                    '\\' -> sb.append('\\')
                    '/' -> sb.append('/')
                    'b' -> sb.append('\b')
                    'f' -> sb.append('\u000C')
                    'n' -> sb.append('\n')
                    'r' -> sb.append('\r')
                    't' -> sb.append('\t')
                    'u' -> {
                        require(pos + 4 < text.length) { "unterminated \\u escape" }
                        val hex = text.substring(pos + 1, pos + 5)
                        sb.append(hex.toInt(16).toChar())
                        pos += 4
                    }
                    else -> throw IllegalArgumentException("bad escape '\\$esc' at position $pos")
                }
                pos++
            } else {
                sb.append(c)
                pos++
            }
        }
        throw IllegalArgumentException("unterminated string at position $pos")
    }

    private fun readBool(): Boolean {
        return when {
            text.startsWith("true", pos) -> { pos += 4; true }
            text.startsWith("false", pos) -> { pos += 5; false }
            else -> throw IllegalArgumentException("expected boolean at position $pos")
        }
    }

    private fun readNull(): Any? {
        require(text.startsWith("null", pos)) { "expected null at position $pos" }
        pos += 4
        return null
    }

    private fun readNumber(): Number {
        val start = pos
        if (peek() == '-') pos++
        while (pos < text.length && (text[pos].isDigit() || text[pos] == '.' ||
                text[pos] == 'e' || text[pos] == 'E' || text[pos] == '+' || text[pos] == '-')) {
            pos++
        }
        val token = text.substring(start, pos)
        require(token.isNotEmpty()) { "expected number at position $pos" }
        return if (token.contains('.') || token.contains('e') || token.contains('E')) {
            token.toDouble()
        } else {
            token.toLong()
        }
    }

    private fun skipWs() {
        while (pos < text.length && text[pos].isWhitespace()) pos++
    }

    private fun peek(): Char {
        require(pos < text.length) { "unexpected end of input at position $pos" }
        return text[pos]
    }
}
