// SPDX-License-Identifier: MIT

package aethernet.uri

import aethernet.identity.AetherNetTag
import java.util.Locale

/**
 * Fluent builder for [AetherUri]. Use when programmatically constructing an
 * Aether URI from parts; for parsing an existing string use [AetherUri.parse].
 *
 * The builder mirrors the C# fluent surface but uses idiomatic Kotlin verbs —
 * [authority], [path], [appendSegment], [query], [removeQuery], [fragment] —
 * instead of the C# `WithXxx` prefix.
 *
 * ## Example
 * ```
 * val uri = AetherUriBuilder()
 *     .authority("KXJB7-MN2P4")
 *     .path("content/sha256-abc123")
 *     .query("codec", "opus")
 *     .fragment("t=1m30s")
 *     .build()
 * // → aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s
 * ```
 */
class AetherUriBuilder {
    private var authority: String? = null
    private var path: String = ""
    private val queryParams = LinkedHashMap<String, String>()
    private var fragment: String = ""

    /** Sets the authority from an [AetherNetTag]. */
    fun authority(tag: AetherNetTag): AetherUriBuilder {
        if (!tag.isValid) throw AetherUriException("AetherTag is uninitialised.")
        authority = tag.value
        return this
    }

    /** Sets the authority from a raw string (AetherTag or 64-char hex UHID). */
    fun authority(value: String): AetherUriBuilder {
        if (value.isEmpty()) throw AetherUriException("Authority is null or empty.")
        // Validate by round-tripping through the parser.
        when (val r = AetherUri.tryParse("aether://$value")) {
            is AetherUri.ParseResult.Ok -> authority = r.uri.authority
            is AetherUri.ParseResult.Err -> throw AetherUriException(r.message)
        }
        return this
    }

    /** Sets the path component (leading slash is stripped). */
    fun path(value: String): AetherUriBuilder {
        path = value.trimStart('/')
        return this
    }

    /** Appends a single segment to the path. */
    fun appendSegment(segment: String): AetherUriBuilder {
        if (segment.isEmpty()) return this
        path = if (path.isEmpty()) segment else "$path/${segment.trimStart('/')}"
        return this
    }

    /** Adds or replaces a query parameter. Keys are lower-cased for case-insensitive lookup. */
    fun query(key: String, value: String): AetherUriBuilder {
        if (key.isEmpty()) throw AetherUriException("Query key is null or empty.")
        queryParams[key.lowercase(Locale.ROOT)] = value
        return this
    }

    /** Removes a query parameter by key. */
    fun removeQuery(key: String): AetherUriBuilder {
        queryParams.remove(key.lowercase(Locale.ROOT))
        return this
    }

    /** Sets the fragment (leading '#' is stripped). */
    fun fragment(value: String): AetherUriBuilder {
        fragment = value.trimStart('#')
        return this
    }

    /**
     * Builds the final [AetherUri]. Throws [AetherUriException] if any component
     * is invalid. The result is canonicalised by round-tripping through the parser.
     */
    fun build(): AetherUri {
        if (authority.isNullOrEmpty()) {
            throw AetherUriException("Authority is required.")
        }
        return AetherUri.parse(toString())
    }

    /**
     * Returns the URI string this builder currently represents. No validation —
     * use [build] for that.
     */
    override fun toString(): String {
        if (authority.isNullOrEmpty()) return ""
        val sb = StringBuilder(64)
        sb.append("aether://")
        sb.append(authority)
        if (path.isNotEmpty()) {
            sb.append('/').append(path)
        }
        if (queryParams.isNotEmpty()) {
            sb.append('?')
            var first = true
            for ((k, v) in queryParams) {
                if (!first) sb.append('&')
                first = false
                sb.append(k)
                if (v.isNotEmpty()) sb.append('=').append(v)
            }
        }
        if (fragment.isNotEmpty()) {
            sb.append('#').append(fragment)
        }
        return sb.toString()
    }
}
