// SPDX-License-Identifier: MIT

package aethernet.uri

import aethernet.identity.AetherNetTag
import java.util.Locale

/**
 * Aether URI — the canonical addressing format for resources on the Aether mesh.
 *
 * ## Grammar (ABNF, RFC 5234)
 * ```
 * aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]
 * authority    = aether-tag / uhid
 * aether-tag   = 5(crockford) [ "-" ] 5(crockford)         ; case-insensitive
 * uhid         = 64(HEXDIG)                                ; SHA-256 hex of public key
 * path         = path-segment *( "/" path-segment )
 * path-segment = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )
 * query        = query-param *( "&" query-param )
 * query-param  = key [ "=" value ]
 * key          = 1*( unreserved / pct-encoded )
 * value        = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
 * fragment     = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
 * crockford    = %x30-39 / %x41-48 / %x4A / %x4B / %x4D / %x4E / %x50-54 / %x56-5A
 *              ; 0–9 A-H J K M N P-T V-Z (no I L O U)
 * unreserved   = ALPHA / DIGIT / "-" / "." / "_" / "~"
 * pct-encoded  = "%" HEXDIG HEXDIG
 * sub-delims   = "!" / "$" / "&" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="
 * ```
 *
 * ## Components
 *
 * - **Scheme** is always `aether`. Case-insensitive on parse, lower-case on emit.
 * - **Authority** identifies the destination — an [AetherNetTag] (10 Crockford
 *   base-32 chars, dash optional) or a UHID (64 hex chars). Case-insensitive.
 * - **Path** is opaque to the protocol — it names a handler within the destination
 *   (e.g. `/content/<hash>`, `/profile`, `/inbox`). Case-sensitive.
 * - **Query** carries handler arguments. Keys are case-insensitive (stored lower-case
 *   for lookup), values are case-sensitive.
 * - **Fragment** is a client-side hint and is never transmitted over the wire.
 *
 * ## Equality
 *
 * Two URIs are equal when their authority, path, fragment, and query map are all
 * equal. Query map equality is order-insensitive — `?a=1&b=2` equals `?b=2&a=1`.
 *
 * ## Examples
 * ```
 * aether://KXJB7-MN2P4/profile
 * aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus
 * aether://KXJB7MN2P4/stream/live?bitrate=hd#t=1m30s
 * aether://a1b2c3d4e5f6...64hex/inbox
 * ```
 *
 * @property authority Destination authority (AetherTag or UHID), canonicalised to upper case.
 * @property path Handler path without the leading slash. Empty string means "root".
 * @property query Decoded query parameters. Keys are stored lower-case for case-insensitive lookup.
 * @property fragment The fragment, with leading "#" stripped. Empty if none.
 */
class AetherUri internal constructor(
    val authority: String,
    val path: String,
    val query: Map<String, String>,
    val fragment: String,
) {

    /** First path segment (handler name). Empty string for root paths. */
    val handlerName: String
        get() {
            if (path.isEmpty()) return ""
            val slash = path.indexOf('/')
            return if (slash >= 0) path.substring(0, slash) else path
        }

    /**
     * Path split into segments (already percent-decoded). Empty list for the root path.
     */
    val pathSegments: List<String>
        get() = if (path.isEmpty()) emptyList() else path.split('/')

    // ── toString — canonical encoder ─────────────────────────────────────────

    /**
     * Returns the canonical string form of this URI. Two URIs that compare equal
     * produce the same canonical string (modulo query-order, which is preserved
     * from the original parse / builder insertion order).
     */
    override fun toString(): String {
        val sb = StringBuilder(64)
        sb.append(SCHEME_PREFIX)
        sb.append(authority)
        if (path.isNotEmpty()) {
            sb.append('/')
            encodePath(sb, path)
        }
        if (query.isNotEmpty()) {
            sb.append('?')
            var first = true
            for ((k, v) in query) {
                if (!first) sb.append('&')
                first = false
                encodeComponent(sb, k, EncodeKind.QUERY_KEY)
                if (v.isNotEmpty()) {
                    sb.append('=')
                    encodeComponent(sb, v, EncodeKind.QUERY_VALUE)
                }
            }
        }
        if (fragment.isNotEmpty()) {
            sb.append('#')
            encodeComponent(sb, fragment, EncodeKind.FRAGMENT)
        }
        return sb.toString()
    }

    // ── Equality (query order-insensitive) ───────────────────────────────────

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is AetherUri) return false
        if (authority != other.authority) return false
        if (path != other.path) return false
        if (fragment != other.fragment) return false
        if (query.size != other.query.size) return false
        for ((k, v) in query) {
            if (other.query[k] != v) return false
        }
        return true
    }

    override fun hashCode(): Int {
        // Combine the same fields the C# struct does. Query is summarised by its
        // size so insertion order doesn't affect the hash — equal URIs always hash
        // to the same value.
        var h = authority.hashCode()
        h = 31 * h + path.hashCode()
        h = 31 * h + fragment.hashCode()
        h = 31 * h + query.size
        return h
    }

    /**
     * Result of a non-throwing [AetherUri.tryParse] call.
     *
     * Use the sealed class form for pattern matching:
     * ```
     * when (val r = AetherUri.tryParse(s)) {
     *     is AetherUri.ParseResult.Ok  -> use(r.uri)
     *     is AetherUri.ParseResult.Err -> log(r.message)
     * }
     * ```
     */
    sealed class ParseResult {
        data class Ok(val uri: AetherUri) : ParseResult()
        data class Err(val message: String) : ParseResult()
    }

    companion object {
        /** The fixed scheme name — `aether`. */
        const val SCHEME = "aether"

        private const val SCHEME_PREFIX = "aether://"

        // ── Parsing ──────────────────────────────────────────────────────────

        /**
         * Parses an `aether://` URI.
         *
         * @throws AetherUriException on any syntactic violation.
         */
        fun parse(input: String): AetherUri =
            when (val r = tryParse(input)) {
                is ParseResult.Ok -> r.uri
                is ParseResult.Err -> throw AetherUriException(r.message)
            }

        /**
         * Attempts to parse an `aether://` URI. Returns a sealed [ParseResult]
         * carrying either the parsed URI or the failure message.
         */
        fun tryParse(input: String): ParseResult {
            if (input.isEmpty()) {
                return ParseResult.Err("Input is null or empty.")
            }

            // Scheme is case-insensitive per RFC 3986.
            if (input.length < SCHEME_PREFIX.length ||
                !input.substring(0, SCHEME_PREFIX.length)
                    .equals(SCHEME_PREFIX, ignoreCase = true)
            ) {
                return ParseResult.Err("Scheme must be '$SCHEME://'.")
            }

            var rest = input.substring(SCHEME_PREFIX.length)

            // Split on fragment first (only one '#' is allowed).
            var fragment = ""
            val fragmentSplit = rest.indexOf('#')
            if (fragmentSplit >= 0) {
                fragment = percentDecode(rest.substring(fragmentSplit + 1))
                rest = rest.substring(0, fragmentSplit)
            }

            // Then query. We use a LinkedHashMap to preserve insertion order so
            // toString() output is deterministic per input.
            val query = LinkedHashMap<String, String>()
            val querySplit = rest.indexOf('?')
            if (querySplit >= 0) {
                val queryRaw = rest.substring(querySplit + 1)
                rest = rest.substring(0, querySplit)
                for (pair in queryRaw.split('&')) {
                    if (pair.isEmpty()) continue
                    val eq = pair.indexOf('=')
                    val rawKey = if (eq >= 0) pair.substring(0, eq) else pair
                    val rawValue = if (eq >= 0) pair.substring(eq + 1) else ""
                    val decodedKey = percentDecode(rawKey)
                    if (decodedKey.isEmpty()) {
                        return ParseResult.Err("Empty query parameter key.")
                    }
                    // Keys are case-insensitive — store lower-case so lookup works
                    // regardless of how the caller spelled them.
                    query[decodedKey.lowercase(Locale.ROOT)] = percentDecode(rawValue)
                }
            }

            // Then path.
            val pathSplit = rest.indexOf('/')
            val authorityRaw: String
            val path: String
            if (pathSplit >= 0) {
                authorityRaw = rest.substring(0, pathSplit)
                path = rest.substring(pathSplit + 1)
            } else {
                authorityRaw = rest
                path = ""
            }

            if (authorityRaw.isEmpty()) {
                return ParseResult.Err("Authority is missing.")
            }

            // Authority validation: either an AetherNetTag or a 64-char hex UHID.
            val authority = canonicaliseAuthority(authorityRaw)
                ?: return ParseResult.Err(
                    "Authority '$authorityRaw' is neither a valid AetherTag nor a 64-char hex UHID."
                )

            // Path validation: segments must contain only allowed characters.
            val pathErr = validatePath(path)
            if (pathErr != null) return ParseResult.Err(pathErr)

            return ParseResult.Ok(AetherUri(authority, percentDecodePath(path), query, fragment))
        }

        private fun canonicaliseAuthority(raw: String): String? {
            // Try UHID (64 hex chars).
            if (raw.length == 64 && isHex(raw)) {
                return raw.uppercase(Locale.ROOT)
            }
            // Try AetherTag (10 Crockford chars with optional dash).
            return AetherNetTag.tryParse(raw)?.value
        }

        private fun isHex(s: String): Boolean {
            for (c in s) {
                if (!((c in '0'..'9') || (c in 'A'..'F') || (c in 'a'..'f'))) return false
            }
            return true
        }

        private fun validatePath(path: String): String? {
            if (path.isEmpty()) return null

            // Walk segments, allow unreserved + pct-encoded + sub-delims + ':' + '@'.
            for (segment in path.split('/')) {
                if (segment.isEmpty()) {
                    return "Empty path segment (consecutive slashes)."
                }
                var i = 0
                while (i < segment.length) {
                    val c = segment[i]
                    if (isUnreserved(c) || isSubDelim(c) || c == ':' || c == '@') {
                        i++
                        continue
                    }
                    if (c == '%') {
                        if (i + 2 >= segment.length ||
                            !isHexChar(segment[i + 1]) ||
                            !isHexChar(segment[i + 2])
                        ) {
                            return "Malformed percent-encoding at position $i of segment '$segment'."
                        }
                        i += 3
                        continue
                    }
                    return "Illegal character '$c' in path segment '$segment'."
                }
            }
            return null
        }

        private fun isUnreserved(c: Char): Boolean =
            (c in 'A'..'Z') || (c in 'a'..'z') || (c in '0'..'9') ||
                c == '-' || c == '.' || c == '_' || c == '~'

        private fun isSubDelim(c: Char): Boolean =
            c == '!' || c == '$' || c == '&' || c == '\'' || c == '(' || c == ')' ||
                c == '*' || c == '+' || c == ',' || c == ';' || c == '='

        private fun isHexChar(c: Char): Boolean =
            (c in '0'..'9') || (c in 'A'..'F') || (c in 'a'..'f')

        // ── Percent codec ────────────────────────────────────────────────────

        private fun percentDecode(input: String): String {
            if (input.indexOf('%') < 0) return input

            val bytes = ArrayList<Byte>(input.length)
            var i = 0
            while (i < input.length) {
                val c = input[i]
                if (c == '%' && i + 2 < input.length &&
                    isHexChar(input[i + 1]) && isHexChar(input[i + 2])
                ) {
                    bytes.add(((hexValue(input[i + 1]) shl 4) or hexValue(input[i + 2])).toByte())
                    i += 3
                } else {
                    // Non-encoded character — emit its UTF-8 bytes.
                    for (b in c.toString().toByteArray(Charsets.UTF_8)) bytes.add(b)
                    i++
                }
            }
            return String(bytes.toByteArray(), Charsets.UTF_8)
        }

        private fun percentDecodePath(path: String): String {
            if (path.isEmpty()) return path
            // Decode each segment independently so '/' isn't lost.
            return path.split('/').joinToString("/") { percentDecode(it) }
        }

        private fun hexValue(c: Char): Int = when {
            c <= '9' -> c - '0'
            c <= 'F' -> c - 'A' + 10
            else -> c - 'a' + 10
        }
    }
}

/** Component-kind selector for the percent-encoder. */
private enum class EncodeKind { PATH_SEGMENT, QUERY_KEY, QUERY_VALUE, FRAGMENT }

private fun encodePath(sb: StringBuilder, path: String) {
    var first = true
    for (segment in path.split('/')) {
        if (!first) sb.append('/')
        first = false
        encodeComponent(sb, segment, EncodeKind.PATH_SEGMENT)
    }
}

private fun encodeComponent(sb: StringBuilder, value: String, kind: EncodeKind) {
    for (c in value) {
        if (isAllowedUnencoded(c, kind)) {
            sb.append(c)
            continue
        }
        // Percent-encode UTF-8 bytes.
        for (b in c.toString().toByteArray(Charsets.UTF_8)) {
            sb.append('%')
            sb.append("%02X".format(b.toInt() and 0xFF))
        }
    }
}

private fun isAllowedUnencoded(c: Char, kind: EncodeKind): Boolean {
    if (isUnreservedChar(c)) return true
    return when (kind) {
        // pchar = unreserved / pct-encoded / sub-delims / ":" / "@"
        EncodeKind.PATH_SEGMENT -> isSubDelimChar(c) || c == ':' || c == '@'
        // Always encode '&' and '=' in keys; allow ':' '@' and the other sub-delims
        // that don't collide with the query syntax.
        EncodeKind.QUERY_KEY -> c == ':' || c == '@' ||
            c == '!' || c == '$' || c == '\'' || c == '(' || c == ')' ||
            c == '*' || c == '+' || c == ',' || c == ';'
        // Allow sub-delims except '&' (separator); '=' is fine inside a value.
        EncodeKind.QUERY_VALUE -> c == ':' || c == '@' || c == '/' || c == '?' ||
            c == '!' || c == '$' || c == '\'' || c == '(' || c == ')' ||
            c == '*' || c == '+' || c == ',' || c == ';' || c == '='
        // fragment = *( pchar / "/" / "?" )  ; pchar incl. ':' '@' sub-delims
        EncodeKind.FRAGMENT -> isSubDelimChar(c) || c == ':' || c == '@' || c == '/' || c == '?'
    }
}

private fun isUnreservedChar(c: Char): Boolean =
    (c in 'A'..'Z') || (c in 'a'..'z') || (c in '0'..'9') ||
        c == '-' || c == '.' || c == '_' || c == '~'

private fun isSubDelimChar(c: Char): Boolean =
    c == '!' || c == '$' || c == '&' || c == '\'' || c == '(' || c == ')' ||
        c == '*' || c == '+' || c == ',' || c == ';' || c == '='

/** Thrown by the AetherUri parser, builder, and manifest when input is invalid. */
class AetherUriException(message: String) : RuntimeException(message)
