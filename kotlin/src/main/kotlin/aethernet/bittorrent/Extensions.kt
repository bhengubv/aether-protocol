// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import java.io.ByteArrayOutputStream
import java.security.MessageDigest

/**
 * The extension protocol (BEP-10), ut_metadata (BEP-9), and ut_pex (BEP-11). The
 * Kotlin port of `go/bittorrent/extensions.go`.
 */

// ── BEP-10 extension protocol ────────────────────────────────────────────────

/** The peer-wire message id for extended messages (BEP-10). */
const val EXTENDED_MESSAGE_ID = 20

/** The extended sub-message id of the handshake. */
const val EXTENSION_HANDSHAKE_ID = 0

/** Builds an extended message payload: [subId][body] — the payload of an Extended (id 20) message. */
fun wrapExtended(subId: Int, body: ByteArray): ByteArray {
    val out = ByteArray(1 + body.size)
    out[0] = subId.toByte()
    System.arraycopy(body, 0, out, 1, body.size)
    return out
}

/** Splits an extended payload into its sub-message id and body. */
fun splitExtended(payload: ByteArray): Pair<Int, ByteArray> {
    require(payload.isNotEmpty()) { "empty extended payload" }
    return (payload[0].toInt() and 0xff) to payload.copyOfRange(1, payload.size)
}

/**
 * Builds a BEP-10 handshake advertising supported extensions (name → local
 * sub-message id) and optionally the metadata size.
 */
fun buildExtensionHandshake(supported: Map<String, Int>, metadataSize: Int): ByteArray {
    val m = BDict()
    for ((name, id) in supported) m.add(name, BInt(id.toLong()))
    val d = BDict()
    d.add("m", m)
    if (metadataSize > 0) d.add("metadata_size", BInt(metadataSize.toLong()))
    return wrapExtended(EXTENSION_HANDSHAKE_ID, Bencode.encode(d))
}

/** A parsed BEP-10 handshake. */
class ExtensionHandshake(val supported: Map<String, Int>, val metadataSize: Int) {
    /** The peer's ut_metadata sub-message id, or 0 if unsupported. */
    fun metadataMessageId(): Int = supported["ut_metadata"] ?: 0

    /** The peer's ut_pex sub-message id, or 0 if unsupported. */
    fun pexMessageId(): Int = supported["ut_pex"] ?: 0
}

/** Parses a BEP-10 handshake body (the bencode dict after the sub-id). */
fun parseExtensionHandshake(body: ByteArray): ExtensionHandshake {
    val supported = LinkedHashMap<String, Int>()
    var metadataSize = 0
    val d = Bencode.decode(body).asDict()
    (d.get("m") as? BDict)?.let { md ->
        for (name in md.keys()) {
            (md.get(name) as? BInt)?.let { supported[name] = it.value.toInt() }
        }
    }
    (d.get("metadata_size") as? BInt)?.let { metadataSize = it.value.toInt() }
    return ExtensionHandshake(supported, metadataSize)
}

// ── BEP-9 ut_metadata ────────────────────────────────────────────────────────

/** A ut_metadata message type. */
object MetadataMessageType {
    const val REQUEST = 0
    const val DATA = 1
    const val REJECT = 2
}

/** The ut_metadata piece size (16 KiB). */
const val METADATA_PIECE_SIZE = 16384

/** Builds a ut_metadata request for a piece. */
fun buildMetadataRequest(piece: Int): ByteArray {
    val d = BDict()
    d.add("msg_type", BInt(MetadataMessageType.REQUEST.toLong()))
    d.add("piece", BInt(piece.toLong()))
    return Bencode.encode(d)
}

/** Builds a ut_metadata data message (bencode header + raw piece bytes). */
fun buildMetadataData(piece: Int, totalSize: Int, data: ByteArray): ByteArray {
    val d = BDict()
    d.add("msg_type", BInt(MetadataMessageType.DATA.toLong()))
    d.add("piece", BInt(piece.toLong()))
    d.add("total_size", BInt(totalSize.toLong()))
    val header = Bencode.encode(d)
    val out = ByteArray(header.size + data.size)
    System.arraycopy(header, 0, out, 0, header.size)
    System.arraycopy(data, 0, out, header.size, data.size)
    return out
}

/** Builds a ut_metadata reject message. */
fun buildMetadataReject(piece: Int): ByteArray {
    val d = BDict()
    d.add("msg_type", BInt(MetadataMessageType.REJECT.toLong()))
    d.add("piece", BInt(piece.toLong()))
    return Bencode.encode(d)
}

/** A parsed ut_metadata message. */
class MetadataMessage(
    val type: Int,
    val piece: Int,
    val totalSize: Int,
    val data: ByteArray,
)

/**
 * Parses a ut_metadata message, splitting the trailing raw piece bytes from the
 * leading bencode dict.
 */
fun parseMetadata(body: ByteArray): MetadataMessage {
    val (v, n) = Bencode.decodeN(body)
    val d = v.asDict()
    val type = (d.get("msg_type") as? BInt)?.value?.toInt() ?: 0
    val piece = (d.get("piece") as? BInt)?.value?.toInt() ?: 0
    val totalSize = (d.get("total_size") as? BInt)?.value?.toInt() ?: 0
    return MetadataMessage(type, piece, totalSize, body.copyOfRange(n, body.size))
}

/**
 * Reassembles the info dictionary from ut_metadata pieces and verifies it against
 * the expected info-hash.
 */
class MetadataAssembler(private val totalSize: Int) {
    private val pieces = HashMap<Int, ByteArray>()

    /** The number of 16 KiB pieces. */
    fun pieceCount(): Int = (totalSize + METADATA_PIECE_SIZE - 1) / METADATA_PIECE_SIZE

    /** Stores a metadata piece. */
    fun add(piece: Int, data: ByteArray) {
        pieces[piece] = data.copyOf()
    }

    /** Whether every piece is present. */
    fun isComplete(): Boolean = pieces.size == pieceCount()

    /** Assembles the info dict and returns it if it matches [infoHash], else null. */
    fun tryFinish(infoHash: ByteArray): ByteArray? {
        if (!isComplete()) return null
        val out = ByteArrayOutputStream(totalSize)
        for (i in 0 until pieceCount()) out.write(pieces[i]!!)
        val assembled = out.toByteArray()
        if (assembled.size != totalSize) return null
        val h = MessageDigest.getInstance("SHA-1").digest(assembled)
        if (!h.contentEquals(infoHash)) return null
        return assembled
    }
}

// ── BEP-11 ut_pex ────────────────────────────────────────────────────────────

/** Builds a ut_pex message advertising added peers (compact form). */
fun buildPexAdded(added: List<PeerAddr>): ByteArray {
    val d = BDict()
    d.add("added", BStr(encodeCompactPeers(added)))
    return Bencode.encode(d)
}

/** Parses the "added" peers from a ut_pex message. */
fun parsePexAdded(body: ByteArray): List<PeerAddr> {
    val d = Bencode.decode(body).asDict()
    val a = d.get("added") ?: return emptyList()
    return decodeCompactPeers(a.asBytes())
}
