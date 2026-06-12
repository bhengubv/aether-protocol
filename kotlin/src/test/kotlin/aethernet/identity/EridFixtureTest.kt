// SPDX-License-Identifier: MIT

package aethernet.identity

import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/**
 * Cross-language ERID parity: the Kotlin port must reproduce the C# reference vectors
 * (fixtures/erid/vectors.json) byte-for-byte. Any drift between Kotlin and the other ports
 * surfaces here as a hex mismatch.
 */
class EridFixtureTest {

    private fun repoRoot(): File {
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "AetherNetProtocol.slnx")
            if (candidate.exists()) return dir!!
            dir = dir?.parentFile ?: return@repeat
        }
        throw IllegalStateException("AetherNetProtocol.slnx not found from ${File(".").canonicalFile}")
    }

    private fun vectors(): JSONObject =
        JSONObject(File(repoRoot(), "fixtures/erid/vectors.json").readText())

    private fun hex(b: ByteArray): String = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    @Test
    fun `erid byte parity with the C# reference fixture`() {
        val v = vectors()
        val eridLength = v.getInt("erid_length")
        val epochSeconds = v.getLong("epoch_seconds")

        val rk = EphemeralRoutingId.deriveRoutingKey(
            v.getString("secret_ascii").toByteArray(Charsets.US_ASCII)
        )
        assertEquals(v.getString("routing_key_hex"), hex(rk), "routingKey")

        val byEpoch = v.getJSONArray("erids_by_epoch")
        for (i in 0 until byEpoch.length()) {
            val o = byEpoch.getJSONObject(i)
            assertEquals(
                o.getString("erid"),
                EphemeralRoutingId.deriveForEpoch(rk, o.getLong("epoch"), eridLength),
                "epoch ${o.getLong("epoch")}",
            )
        }

        val byUnix = v.getJSONArray("derive_by_unixseconds")
        for (i in 0 until byUnix.length()) {
            val o = byUnix.getJSONObject(i)
            assertEquals(
                o.getString("erid"),
                EphemeralRoutingId.derive(rk, o.getLong("unix"), epochSeconds, eridLength),
                "unix ${o.getLong("unix")}",
            )
        }

        val enc = EridAnnouncementCodec.encode(rk, epochSeconds.toInt(), eridLength)
        assertEquals(v.getString("announcement_encode_hex"), hex(enc), "announcement frame")

        // Round-trip the frame back through the decoder.
        val dec = assertNotNull(EridAnnouncementCodec.tryDecode(enc))
        assertEquals(v.getString("routing_key_hex"), hex(dec.routingKey))
        assertEquals(epochSeconds.toInt(), dec.epochSeconds)
        assertEquals(eridLength, dec.eridLength)
    }

    @Test
    fun `erid directory resolves an established peer but not an outsider`() {
        val aKey = EphemeralRoutingId.deriveRoutingKey("identity-A".toByteArray())
        val bKey = EphemeralRoutingId.deriveRoutingKey("identity-B".toByteArray())
        val alice = EridDirectory(aKey)
        val bob = EridDirectory(bKey)
        alice.rememberPeer("bob", bKey)
        bob.rememberPeer("alice", aKey)
        val t = 1_700_000_000L

        // An established peer resolves the other's rotating address, both directions.
        assertEquals(bob.myErid(t), alice.eridForPeer("bob", t))
        assertEquals("alice", bob.resolvePeer(alice.myErid(t), t))

        // An outsider holding no routingKey cannot.
        val outsider = EridDirectory(EphemeralRoutingId.deriveRoutingKey("identity-X".toByteArray()))
        assertNull(outsider.resolvePeer(alice.myErid(t), t))
    }
}
