// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import java.io.ByteArrayOutputStream

/**
 * From-scratch, interoperable bencoding (BEP-3) — the Kotlin port of the Go
 * reference in `go/bittorrent/bencode.go` and the C# `AetherNet.BitTorrent.Bencoding`.
 * Encoded bytes are byte-identical to every other AetherNet language SDK: strict
 * decode (rejects leading zeros, negative zero, duplicate/unsorted keys, trailing
 * data, overflow) and canonical encode (dictionary keys emitted sorted by raw,
 * unsigned byte order).
 *
 * Byte strings hold RAW bytes — they are NOT necessarily UTF-8 text.
 */

/** Any BEP-3 bencoding violation. */
class BencodeException(message: String) : Exception(message)

/** A decoded bencode value: an integer, a byte string, a list, or a dictionary. */
sealed interface BencodeValue

/** A bencode integer (`i<decimal>e`, 64-bit signed). */
class BInt(val value: Long) : BencodeValue

/** A bencode byte string (`<length>:<bytes>`). Raw bytes, not necessarily text. */
class BStr(val bytes: ByteArray) : BencodeValue {
    /** Convenience constructor for UTF-8 text keys/values. */
    constructor(text: String) : this(text.toByteArray(Charsets.UTF_8))
}

/** A bencode list (`l<values…>e`). */
class BList(val items: List<BencodeValue>) : BencodeValue

/**
 * A bencode dictionary: keys are byte strings, unique, emitted sorted by raw
 * (unsigned) byte order per BEP-3. Insertion order is preserved internally; the
 * canonical sort is applied only at encode time (mirrors the Go reference).
 */
class BDict : BencodeValue {
    private val keyList = ArrayList<String>()
    private val valueList = ArrayList<BencodeValue>()
    private val lookup = HashMap<String, Int>()

    /** Inserts a key/value, rejecting duplicate keys. Returns this for chaining. */
    fun add(key: String, value: BencodeValue): BDict {
        if (lookup.containsKey(key)) throw BencodeException("duplicate dictionary key \"$key\"")
        lookup[key] = keyList.size
        keyList.add(key)
        valueList.add(value)
        return this
    }

    /** Returns the value for a key, or null if absent. */
    fun get(key: String): BencodeValue? {
        val i = lookup[key] ?: return null
        return valueList[i]
    }

    /** The dictionary keys in insertion order. */
    fun keys(): List<String> = keyList.toList()

    /** The number of entries. */
    val size: Int get() = keyList.size

    /** (key, value) pairs ordered by canonical (unsigned byte) key order — encode order. */
    internal fun sortedEntries(): List<Pair<ByteArray, BencodeValue>> {
        val indices = keyList.indices.sortedWith(
            Comparator { a, b ->
                compareBytesUnsigned(
                    keyList[a].toByteArray(Charsets.UTF_8),
                    keyList[b].toByteArray(Charsets.UTF_8),
                )
            },
        )
        return indices.map { keyList[it].toByteArray(Charsets.UTF_8) to valueList[it] }
    }
}

// ── typed accessors ──────────────────────────────────────────────────────────

/** Returns the int64 value or throws if this is not an integer. */
fun BencodeValue.asInt(): Long =
    (this as? BInt)?.value ?: throw BencodeException("value is not an integer")

/** Returns the raw bytes or throws if this is not a byte string. */
fun BencodeValue.asBytes(): ByteArray =
    (this as? BStr)?.bytes ?: throw BencodeException("value is not a byte string")

/** Returns the value interpreted as UTF-8 text. */
fun BencodeValue.asText(): String = String(asBytes(), Charsets.UTF_8)

/** Returns the list items or throws if this is not a list. */
fun BencodeValue.asList(): List<BencodeValue> =
    (this as? BList)?.items ?: throw BencodeException("value is not a list")

/** Returns the dictionary or throws if this is not a dictionary. */
fun BencodeValue.asDict(): BDict =
    (this as? BDict) ?: throw BencodeException("value is not a dictionary")

/** Bencode encode/decode entry points. */
object Bencode {

    /** Returns the canonical bencoding of [v] (dictionary keys sorted by raw byte order). */
    fun encode(v: BencodeValue): ByteArray {
        val out = ByteArrayOutputStream()
        encodeTo(v, out)
        return out.toByteArray()
    }

    private fun encodeTo(v: BencodeValue, out: ByteArrayOutputStream) {
        when (v) {
            is BInt -> {
                out.write('i'.code)
                out.write(v.value.toString().toByteArray(Charsets.US_ASCII))
                out.write('e'.code)
            }
            is BStr -> {
                out.write(v.bytes.size.toString().toByteArray(Charsets.US_ASCII))
                out.write(':'.code)
                out.write(v.bytes)
            }
            is BList -> {
                out.write('l'.code)
                for (item in v.items) encodeTo(item, out)
                out.write('e'.code)
            }
            is BDict -> {
                out.write('d'.code)
                for ((key, value) in v.sortedEntries()) {
                    out.write(key.size.toString().toByteArray(Charsets.US_ASCII))
                    out.write(':'.code)
                    out.write(key)
                    encodeTo(value, out)
                }
                out.write('e'.code)
            }
        }
    }

    /** Parses a single bencode value and rejects any trailing data. */
    fun decode(data: ByteArray): BencodeValue {
        val r = BencodeReader(data)
        val v = r.readValue()
        if (r.pos != data.size) {
            throw BencodeException("${data.size - r.pos} trailing byte(s) after value")
        }
        return v
    }

    /** Parses one bencode value and returns it with the number of bytes consumed. */
    fun decodeN(data: ByteArray): Pair<BencodeValue, Int> {
        val r = BencodeReader(data)
        val v = r.readValue()
        return v to r.pos
    }
}

/**
 * Recursive-descent bencode reader with an absolute byte cursor. Kept internal so
 * metainfo parsing can reuse it for byte-offset extraction of the raw info dict.
 */
internal class BencodeReader(private val data: ByteArray) {
    var pos = 0

    fun readValue(): BencodeValue {
        if (pos >= data.size) throw BencodeException("empty input")
        val c = data[pos].toInt() and 0xff
        return when {
            c == 'i'.code -> readInt()
            c == 'l'.code -> readList()
            c == 'd'.code -> readDict()
            c in '0'.code..'9'.code -> readString()
            else -> throw BencodeException("unexpected byte 0x${c.toString(16).padStart(2, '0')}")
        }
    }

    private fun readInt(): BInt {
        var e = pos + 1
        while (e < data.size && (data[e].toInt() and 0xff) != 'e'.code) e++
        if (e >= data.size) throw BencodeException("integer has no terminating 'e'")
        val body = String(data, pos + 1, e - (pos + 1), Charsets.US_ASCII)
        if (body.isEmpty()) throw BencodeException("empty integer")
        if (body == "-0") throw BencodeException("negative zero is not allowed")
        var digits = body
        if (digits[0] == '-') {
            digits = digits.substring(1)
            if (digits.isEmpty()) throw BencodeException("bare minus sign")
        }
        if (digits.length > 1 && digits[0] == '0') throw BencodeException("integer has a leading zero")
        for (ch in digits) {
            if (ch < '0' || ch > '9') throw BencodeException("integer has a non-digit")
        }
        val value = body.toLongOrNull() ?: throw BencodeException("integer overflow: $body")
        pos = e + 1
        return BInt(value)
    }

    fun readString(): BStr {
        var colon = pos
        while (colon < data.size && (data[colon].toInt() and 0xff) != ':'.code) colon++
        if (colon >= data.size) throw BencodeException("byte string has no ':'")
        val lenLen = colon - pos
        if (lenLen == 0) throw BencodeException("byte string has an empty length")
        if (lenLen > 1 && (data[pos].toInt() and 0xff) == '0'.code) {
            throw BencodeException("byte-string length has a leading zero")
        }
        var n = 0L
        for (i in pos until colon) {
            val ch = data[i].toInt() and 0xff
            if (ch < '0'.code || ch > '9'.code) throw BencodeException("byte-string length has a non-digit")
            n = n * 10 + (ch - '0'.code)
            if (n > Int.MAX_VALUE) throw BencodeException("byte-string length overflow")
        }
        val start = colon + 1
        if (start + n > data.size) throw BencodeException("byte string runs past end of input")
        val out = data.copyOfRange(start, (start + n).toInt())
        pos = start + n.toInt()
        return BStr(out)
    }

    private fun readList(): BList {
        pos++ // skip 'l'
        val items = ArrayList<BencodeValue>()
        while (true) {
            if (pos >= data.size) throw BencodeException("list has no terminating 'e'")
            if ((data[pos].toInt() and 0xff) == 'e'.code) {
                pos++
                return BList(items)
            }
            items.add(readValue())
        }
    }

    private fun readDict(): BDict {
        pos++ // skip 'd'
        val d = BDict()
        var prevKey: ByteArray? = null
        while (true) {
            if (pos >= data.size) throw BencodeException("dictionary has no terminating 'e'")
            if ((data[pos].toInt() and 0xff) == 'e'.code) {
                pos++
                return d
            }
            val c = data[pos].toInt() and 0xff
            if (c < '0'.code || c > '9'.code) throw BencodeException("dictionary key must be a byte string")
            val key = readString().bytes
            val prev = prevKey
            if (prev != null) {
                val cmp = compareBytesUnsigned(prev, key)
                if (cmp == 0) throw BencodeException("duplicate dictionary key")
                if (cmp > 0) throw BencodeException("dictionary keys are not sorted")
            }
            prevKey = key
            if (pos >= data.size) throw BencodeException("dictionary key without a value")
            val value = readValue()
            d.add(String(key, Charsets.UTF_8), value)
        }
    }
}
