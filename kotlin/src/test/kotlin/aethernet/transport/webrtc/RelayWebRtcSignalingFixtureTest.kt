// SPDX-License-Identifier: MIT

package aethernet.transport.webrtc

import org.json.JSONArray
import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals

/**
 * Cross-language WebRTC-signalling framing parity: the Kotlin port must reproduce the shared
 * oracle's byte vectors (fixtures/webrtc/expected/<name>.bin) byte-for-byte for every case in
 * fixtures/webrtc/inputs.json, and deframe each back to matching fields. Any drift between Kotlin
 * and the other ports surfaces here as a byte mismatch.
 *
 * Mirrors RelayFrameFixtureTest exactly: same repo-root resolution, same org.json parsing
 * (Soong-compatible; no kotlinx.serialization), same assertion surface.
 */
class RelayWebRtcSignalingFixtureTest {

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
        JSONArray(File(repoRoot(), "fixtures/webrtc/inputs.json").readText())

    private fun expected(name: String): ByteArray =
        File(repoRoot(), "fixtures/webrtc/expected/$name.bin").readBytes()

    /** type ordinal 0/1/2 -> OFFER/ANSWER/ICE_CANDIDATE. */
    private fun typeOf(o: JSONObject): WebRtcSignalType = when (o.getInt("type")) {
        0 -> WebRtcSignalType.OFFER
        1 -> WebRtcSignalType.ANSWER
        2 -> WebRtcSignalType.ICE_CANDIDATE
        else -> throw IllegalArgumentException("bad type ${o.getInt("type")}")
    }

    /** Missing/empty sdp/candidate/sdp_mid → null (omitted on the wire); else the string value. */
    private fun optOrNull(o: JSONObject, key: String): String? {
        val s = o.optString(key, "")
        return if (s.isEmpty()) null else s
    }

    private fun signalFromInput(o: JSONObject): WebRtcSignal = WebRtcSignal(
        fromUhid = o.getString("from_uhid"),
        toUhid = o.getString("to_uhid"),
        type = typeOf(o),
        sdp = optOrNull(o, "sdp"),
        candidate = optOrNull(o, "candidate"),
        sdpMid = optOrNull(o, "sdp_mid"),
        sdpMLineIndex = o.optInt("sdp_mline_index", 0),
    )

    /** Every input case frames byte-for-byte to its shared-oracle .bin vector. */
    @Test
    fun `webrtc signal frames byte-identical to the shared oracle`() {
        val arr = inputs()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val name = o.getString("name")
            assertContentEquals(
                expected(name),
                RelayWebRtcSignaling.frame(signalFromInput(o)),
                "$name: frame byte mismatch",
            )
        }
    }

    /** Every .bin vector deframes back to the input fields. */
    @Test
    fun `webrtc signal deframes every field round-trip`() {
        val arr = inputs()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val name = o.getString("name")
            val s = RelayWebRtcSignaling.deframe(expected(name))

            assertEquals(o.getString("from_uhid"), s.fromUhid, "$name from_uhid")
            assertEquals(o.getString("to_uhid"), s.toUhid, "$name to_uhid")
            assertEquals(typeOf(o), s.type, "$name type")
            assertEquals(optOrNull(o, "sdp"), s.sdp, "$name sdp")
            assertEquals(optOrNull(o, "candidate"), s.candidate, "$name candidate")
            assertEquals(optOrNull(o, "sdp_mid"), s.sdpMid, "$name sdp_mid")
            assertEquals(o.optInt("sdp_mline_index", 0), s.sdpMLineIndex, "$name sdp_mline_index")
        }
    }
}
