// SPDX-License-Identifier: MIT

package aethernet.identity

import org.json.JSONArray
import org.junit.jupiter.api.DynamicTest
import org.junit.jupiter.api.DynamicTest.dynamicTest
import org.junit.jupiter.api.TestFactory
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Cross-language PeerID parity: the Kotlin port must reproduce the shared corpus that every
 * AetherNet SDK consumes (`fixtures/peerid/`) — itself verified byte-for-byte against real
 * js-libp2p output. Any drift between Kotlin and the other ports surfaces here as a string
 * mismatch.
 *
 * Corpus layout (do not modify):
 *  - `fixtures/peerid/inputs.json` — array of `{ name, pubkey_hex }`.
 *  - `fixtures/peerid/expected/<name>.txt` — the exact PeerID string for that case.
 */
class PeerIdFixtureTest {

    private fun repoRoot(): File {
        var dir: File? = File(".").canonicalFile
        repeat(10) {
            val candidate = File(dir, "AetherNetProtocol.slnx")
            if (candidate.exists()) return dir!!
            dir = dir?.parentFile ?: return@repeat
        }
        throw IllegalStateException("AetherNetProtocol.slnx not found from ${File(".").canonicalFile}")
    }

    private fun inputs(): JSONArray =
        JSONArray(File(repoRoot(), "fixtures/peerid/inputs.json").readText())

    private fun hexToBytes(hex: String): ByteArray =
        ByteArray(hex.length / 2) { i -> hex.substring(i * 2, i * 2 + 2).toInt(16).toByte() }

    @TestFactory
    fun `peerid byte parity with the js-libp2p corpus`(): List<DynamicTest> {
        val cases = inputs()
        return (0 until cases.length()).map { i ->
            val obj = cases.getJSONObject(i)
            val name = obj.getString("name")
            dynamicTest("peerid: $name") {
                val pubkey = hexToBytes(obj.getString("pubkey_hex"))
                val expected = File(repoRoot(), "fixtures/peerid/expected/$name.txt").readText().trim()

                val actual = PeerId.fromEd25519PublicKey(pubkey)
                assertEquals(expected, actual, name)
                assertTrue(actual.startsWith("12D3Koo"), "$name: PeerID must start with 12D3Koo, got $actual")
            }
        }
    }
}
