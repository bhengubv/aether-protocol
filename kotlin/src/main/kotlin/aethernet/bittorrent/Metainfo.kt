// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import java.security.MessageDigest

/** One file within a torrent: its path components and length. */
data class TorrentFileEntry(val path: List<String>, val length: Long) {
    /** The path components joined with '/'. */
    fun joinedPath(): String = path.joinToString("/")
}

/**
 * A parsed BitTorrent v1 metainfo (.torrent). [infoHashV1] is the SHA-1 of the RAW
 * bencoded info dictionary as it appears in the file (extracted by byte offset, NOT
 * a re-encode), so it matches real clients byte-for-byte. The Kotlin port of the Go
 * `go/bittorrent/metainfo.go` and the C# `AetherNet.BitTorrent.Metainfo`.
 */
class TorrentMetainfo(
    val root: BDict,
    val info: BDict,
    val infoHashV1: ByteArray,
    val name: String,
    val pieceLength: Long,
    val pieceHashes: List<ByteArray>,
    val files: List<TorrentFileEntry>,
    val totalLength: Long,
    val announceUrls: List<String>,
    val isSingleFile: Boolean,
) {
    /** The lowercase hex of [infoHashV1] (40 chars). */
    fun infoHashV1Hex(): String = toHexLower(infoHashV1)
}

private fun sha1(data: ByteArray): ByteArray =
    MessageDigest.getInstance("SHA-1").digest(data)

private fun toHexLower(b: ByteArray): String {
    val sb = StringBuilder(b.size * 2)
    for (x in b) {
        val v = x.toInt() and 0xff
        sb.append("0123456789abcdef"[v ushr 4])
        sb.append("0123456789abcdef"[v and 0xf])
    }
    return sb.toString()
}

/**
 * Creates single-file .torrent bytes for [data], splitting into [pieceLength]-byte
 * pieces and SHA-1-hashing each. Byte-identical to the reference TorrentBuilder.
 */
fun buildSingleFileTorrent(name: String, data: ByteArray, pieceLength: Int, announce: String): ByteArray {
    require(name.isNotEmpty()) { "name is required" }
    require(pieceLength > 0) { "piece length must be positive" }
    val pieceCount = (data.size + pieceLength - 1) / pieceLength
    val pieces = ByteArray(pieceCount * 20)
    for (i in 0 until pieceCount) {
        val start = i * pieceLength
        val end = minOf(start + pieceLength, data.size)
        val h = sha1(data.copyOfRange(start, end))
        System.arraycopy(h, 0, pieces, i * 20, 20)
    }

    val info = BDict()
    info.add("length", BInt(data.size.toLong()))
    info.add("name", BStr(name))
    info.add("piece length", BInt(pieceLength.toLong()))
    info.add("pieces", BStr(pieces))

    val root = BDict()
    if (announce.isNotBlank()) root.add("announce", BStr(announce))
    root.add("info", info)
    return Bencode.encode(root)
}

/** Parses .torrent bytes into a [TorrentMetainfo]. */
fun parseTorrent(data: ByteArray): TorrentMetainfo {
    val root = Bencode.decode(data).asDict()
    val info = (root.get("info") ?: throw BencodeException("metainfo has no 'info' dictionary")).asDict()

    val infoSpan = extractInfoSpan(data)
    val infoHash = sha1(infoSpan)

    val name = (info.get("name") ?: throw BencodeException("info has no 'name'")).asText()

    val pieceLength = (info.get("piece length") ?: throw BencodeException("info has no 'piece length'")).asInt()
    if (pieceLength <= 0) throw BencodeException("'piece length' must be positive")

    val piecesBytes = (info.get("pieces") ?: throw BencodeException("info has no 'pieces'")).asBytes()
    if (piecesBytes.size % 20 != 0) {
        throw BencodeException("'pieces' length ${piecesBytes.size} is not a multiple of 20")
    }
    val pieceHashes = ArrayList<ByteArray>(piecesBytes.size / 20)
    var i = 0
    while (i < piecesBytes.size) {
        pieceHashes.add(piecesBytes.copyOfRange(i, i + 20))
        i += 20
    }

    val files = ArrayList<TorrentFileEntry>()
    var total = 0L
    var singleFile = false
    val filesVal = info.get("files")
    if (filesVal != null) {
        for (f in filesVal.asList()) {
            val fd = f.asDict()
            val length = (fd.get("length") ?: throw BencodeException("file entry has no 'length'")).asInt()
            val pathList = (fd.get("path") ?: throw BencodeException("file entry has no 'path'")).asList()
            val parts = pathList.map { it.asText() }
            if (parts.isEmpty()) throw BencodeException("file entry has an empty 'path'")
            files.add(TorrentFileEntry(parts, length))
            total += length
        }
    } else {
        singleFile = true
        val length = (info.get("length")
            ?: throw BencodeException("single-file info has neither 'length' nor 'files'")).asInt()
        files.add(TorrentFileEntry(listOf(name), length))
        total = length
    }

    // Trackers: announce + announce-list, de-duplicated, order preserved.
    val announce = ArrayList<String>()
    val seen = HashSet<String>()
    fun add(u: String) {
        if (u.isNotEmpty() && seen.add(u)) announce.add(u)
    }
    (root.get("announce") as? BStr)?.let { add(it.asText()) }
    (root.get("announce-list") as? BList)?.let { al ->
        for (tier in al.items) {
            (tier as? BList)?.let { ts ->
                for (t in ts.items) (t as? BStr)?.let { add(it.asText()) }
            }
        }
    }

    return TorrentMetainfo(
        root = root,
        info = info,
        infoHashV1 = infoHash,
        name = name,
        pieceLength = pieceLength,
        pieceHashes = pieceHashes,
        files = files,
        totalLength = total,
        announceUrls = announce,
        isSingleFile = singleFile,
    )
}

/**
 * Returns the raw bencoded bytes of the top-level "info" value by walking the
 * dictionary with byte-offset tracking (structure already validated by [parseTorrent]).
 */
internal fun extractInfoSpan(data: ByteArray): ByteArray {
    if (data.isEmpty() || (data[0].toInt() and 0xff) != 'd'.code) {
        throw BencodeException("metainfo is not a bencoded dictionary")
    }
    val r = BencodeReader(data)
    r.pos = 1
    while (r.pos < data.size && (data[r.pos].toInt() and 0xff) != 'e'.code) {
        val key = r.readValue().asBytes()
        val valStart = r.pos
        r.readValue()
        val valEnd = r.pos
        if (String(key, Charsets.UTF_8) == "info") return data.copyOfRange(valStart, valEnd)
    }
    throw BencodeException("metainfo has no 'info' key")
}
