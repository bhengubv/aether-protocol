// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import java.net.URLDecoder

/**
 * A parsed magnet: URI (BEP-9 `xt=urn:btih:`). Accepts a 40-char hex or 32-char
 * base32 info-hash. The Kotlin port of `go/bittorrent/magnet.go`.
 */
class MagnetLink(
    val infoHash: ByteArray,
    val displayName: String,
    val trackers: List<String>,
) {
    /** The lowercase hex of the info-hash (40 chars). */
    fun infoHashHex(): String {
        val sb = StringBuilder(40)
        for (x in infoHash) {
            val v = x.toInt() and 0xff
            sb.append("0123456789abcdef"[v ushr 4])
            sb.append("0123456789abcdef"[v and 0xf])
        }
        return sb.toString()
    }
}

/** Parses a magnet URI. */
fun parseMagnet(uri: String): MagnetLink {
    val prefix = "magnet:?"
    require(uri.startsWith(prefix)) { "not a magnet URI" }
    val query = parseQuery(uri.substring(prefix.length))

    var hash: ByteArray? = null
    for (xt in query["xt"] ?: emptyList()) {
        val btih = "urn:btih:"
        if (xt.startsWith(btih)) {
            hash = decodeInfoHash(xt.substring(btih.length))
            break
        }
    }
    requireNotNull(hash) { "magnet has no xt=urn:btih: topic" }

    return MagnetLink(
        infoHash = hash,
        displayName = query["dn"]?.firstOrNull() ?: "",
        trackers = query["tr"] ?: emptyList(),
    )
}

private fun decodeInfoHash(s: String): ByteArray = when (s.length) {
    40 -> hexDecode(s)
    32 -> base32Decode(s.uppercase())
    else -> throw IllegalArgumentException("info-hash must be 40 hex or 32 base32 chars, got ${s.length}")
}

private fun parseQuery(raw: String): Map<String, List<String>> {
    val out = LinkedHashMap<String, MutableList<String>>()
    if (raw.isEmpty()) return out
    for (pair in raw.split("&")) {
        if (pair.isEmpty()) continue
        val eq = pair.indexOf('=')
        val key: String
        val value: String
        if (eq < 0) {
            key = urlDecode(pair)
            value = ""
        } else {
            key = urlDecode(pair.substring(0, eq))
            value = urlDecode(pair.substring(eq + 1))
        }
        out.getOrPut(key) { ArrayList() }.add(value)
    }
    return out
}

private fun urlDecode(s: String): String = URLDecoder.decode(s, "UTF-8")

private fun hexDecode(s: String): ByteArray {
    require(s.length % 2 == 0) { "invalid hex info-hash" }
    return ByteArray(s.length / 2) { i ->
        val hi = Character.digit(s[i * 2], 16)
        val lo = Character.digit(s[i * 2 + 1], 16)
        require(hi >= 0 && lo >= 0) { "invalid hex info-hash" }
        ((hi shl 4) or lo).toByte()
    }
}

private const val BASE32_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"

/** RFC-4648 base32 decode with no padding (uppercase input). */
private fun base32Decode(s: String): ByteArray {
    var buffer = 0
    var bitsLeft = 0
    val out = ArrayList<Byte>((s.length * 5) / 8)
    for (ch in s) {
        val v = BASE32_ALPHABET.indexOf(ch)
        require(v >= 0) { "invalid base32 info-hash" }
        buffer = (buffer shl 5) or v
        bitsLeft += 5
        if (bitsLeft >= 8) {
            bitsLeft -= 8
            out.add(((buffer ushr bitsLeft) and 0xff).toByte())
        }
    }
    return out.toByteArray()
}
