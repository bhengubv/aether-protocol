// SPDX-License-Identifier: MIT

package aethernet.bittorrent

import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals

/**
 * Cross-language BitTorrent fixture verifier: asserts this Kotlin port reproduces
 * every vector in `fixtures/bittorrent/vectors.json` byte-for-byte. A direct mirror
 * of the Go oracle in `go/bittorrent/fixture_test.go`; Go/C#/Python/TS/Swift all
 * ship the equivalent test, so any wire drift fails on the language that diverges.
 *
 * The corpus is the truth — if a category fails here, the Kotlin port is wrong.
 *
 * JSON is loaded with org.json (the Soong-compatible reader the other Kotlin
 * fixture tests use), walking up to the repo root like the DTN/bandwidth drivers.
 */
class BitTorrentFixtureTest {

    private fun corpus(): JSONObject {
        var dir: File? = File(".").canonicalFile
        repeat(12) {
            val candidate = File(dir, "fixtures/bittorrent/vectors.json")
            if (candidate.exists()) return JSONObject(candidate.readText())
            dir = dir?.parentFile ?: return@repeat
        }
        error("Could not locate fixtures/bittorrent/vectors.json from ${File(".").canonicalPath}")
    }

    private fun unhex(s: String): ByteArray {
        if (s.isEmpty()) return ByteArray(0)
        return ByteArray(s.length / 2) { i ->
            ((Character.digit(s[i * 2], 16) shl 4) + Character.digit(s[i * 2 + 1], 16)).toByte()
        }
    }

    private fun hex(b: ByteArray): String {
        val sb = StringBuilder(b.size * 2)
        for (x in b) {
            val v = x.toInt() and 0xff
            sb.append("0123456789abcdef"[v ushr 4])
            sb.append("0123456789abcdef"[v and 0xf])
        }
        return sb.toString()
    }

    /** Content generator shared by info_hash and merkle vectors: byte[i] = (i*mult+add) & 0xFF. */
    private fun fillBytes(n: Int, mult: Int, add: Int): ByteArray =
        ByteArray(n) { i -> ((i * mult + add) and 0xff).toByte() }

    // ── bencode_roundtrip ─────────────────────────────────────────────────────

    @Test
    fun `bencode round-trips every vector byte-identical`() {
        val arr = corpus().getJSONArray("bencode_roundtrip")
        for (i in 0 until arr.length()) {
            val hs = arr.getString(i)
            val decoded = Bencode.decode(unhex(hs))
            assertEquals(hs, hex(Bencode.encode(decoded)), "bencode roundtrip $hs")
        }
    }

    // ── info_hash ─────────────────────────────────────────────────────────────

    @Test
    fun `info-hash matches for every built torrent`() {
        val arr = corpus().getJSONArray("info_hash")
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val content = fillBytes(o.getInt("size"), o.getInt("mult"), o.getInt("add"))
            val torrent = buildSingleFileTorrent(
                o.getString("name_str"),
                content,
                o.getInt("piece_length"),
                "",
            )
            val m = parseTorrent(torrent)
            assertEquals(o.getString("info_hash_hex"), m.infoHashV1Hex(), "${o.getString("name")} info-hash")
        }
    }

    // ── peer_messages ─────────────────────────────────────────────────────────

    @Test
    fun `peer messages serialise to expected wire bytes`() {
        val arr = corpus().getJSONArray("peer_messages")
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val a = o.getLong("a")
            val b = o.getLong("b")
            val c = o.getLong("c")
            val msg = when (val kind = o.getString("kind")) {
                "keepalive" -> PeerMessage.keepAlive()
                "choke" -> PeerMessage.choke()
                "unchoke" -> PeerMessage.unchoke()
                "interested" -> PeerMessage.interested()
                "have" -> PeerMessage.have(a)
                "request" -> PeerMessage.request(a, b, c)
                "port" -> PeerMessage.port(a.toInt())
                else -> error("unknown kind $kind")
            }
            assertEquals(o.getString("wire_hex"), hex(msg.toBytes()), "${o.getString("name")} wire")
        }
    }

    // ── utp_packets ───────────────────────────────────────────────────────────

    @Test
    fun `utp packets serialise to expected wire bytes`() {
        val arr = corpus().getJSONArray("utp_packets")
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val p = UtpPacket(
                type = o.getInt("type"),
                connectionId = o.getInt("conn_id"),
                timestampMicros = o.getLong("timestamp"),
                timestampDiff = o.getLong("timestamp_diff"),
                windowSize = o.getLong("window"),
                seqNr = o.getInt("seq"),
                ackNr = o.getInt("ack"),
                payload = unhex(o.getString("payload_hex")),
            )
            assertEquals(o.getString("wire_hex"), hex(p.toBytes()), "${o.getString("name")} wire")
        }
    }

    // ── merkle ────────────────────────────────────────────────────────────────

    @Test
    fun `merkle roots match for every vector`() {
        val arr = corpus().getJSONArray("merkle")
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val content = fillBytes(o.getInt("size"), o.getInt("mult"), o.getInt("add"))
            assertEquals(o.getString("root_hex"), hex(merkleRoot(content)), "${o.getString("name")} root")
        }
    }

    // ── compact ───────────────────────────────────────────────────────────────

    @Test
    fun `compact node and peer records match`() {
        val arr = corpus().getJSONArray("compact")
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val wireHex = o.getString("wire_hex")
            when (val kind = o.getString("kind")) {
                "node" -> {
                    // decode + re-encode round-trip
                    val nodes = decodeCompactNodes(unhex(wireHex))
                    assertEquals(wireHex, hex(encodeCompactNodes(nodes)), "${o.getString("name")} node roundtrip")
                }
                "peers" -> {
                    // build from the structured list → wire
                    val peersArr = o.getJSONArray("peers")
                    val peers = (0 until peersArr.length()).map { j ->
                        val pj = peersArr.getJSONObject(j)
                        PeerAddr.fromString(pj.getString("ip"), pj.getInt("port"))
                    }
                    assertEquals(wireHex, hex(encodeCompactPeers(peers)), "${o.getString("name")} peers build")
                }
                else -> error("unknown compact kind $kind")
            }
        }
    }

    // ── krpc ──────────────────────────────────────────────────────────────────

    @Test
    fun `krpc messages encode to expected wire bytes`() {
        val arr = corpus().getJSONArray("krpc")
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val tx = unhex(o.getString("tx_hex"))
            val msg = when (val kind = o.getString("kind")) {
                "get_peers" -> {
                    val args = BDict()
                    args.add("id", BStr(unhex(o.getString("id_hex"))))
                    args.add("info_hash", BStr(unhex(o.getString("info_hash_hex"))))
                    KrpcMessage(tx, KrpcType.QUERY, method = "get_peers", arguments = args)
                }
                "error" -> KrpcMessage(
                    tx,
                    KrpcType.ERROR,
                    errorCode = o.getLong("error_code"),
                    errorMessage = o.getString("error_message"),
                )
                else -> error("unknown krpc kind $kind")
            }
            assertEquals(o.getString("wire_hex"), hex(msg.encode()), "${o.getString("name")} krpc")
        }
    }
}
