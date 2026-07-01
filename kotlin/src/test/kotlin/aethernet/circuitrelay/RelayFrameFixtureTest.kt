// SPDX-License-Identifier: MIT

package aethernet.circuitrelay

import org.json.JSONArray
import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import java.util.UUID
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals

/**
 * Cross-language circuit-relay-v2 parity: the Kotlin port must reproduce the Go
 * oracle's byte vectors (fixtures/circuit-relay/expected/<name>.bin) byte-for-byte
 * for every case in fixtures/circuit-relay/inputs.json, and deserialize each back
 * to matching fields. Any drift between Kotlin and the other seven ports surfaces
 * here as a byte mismatch.
 *
 * Mirrors DtnEnvelopeFixtureTest exactly: same repo-root resolution, same org.json
 * parsing (Soong-compatible; no kotlinx.serialization), same assertion surface.
 */
class RelayFrameFixtureTest {

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
        JSONArray(File(repoRoot(), "fixtures/circuit-relay/inputs.json").readText())

    private fun expected(name: String): ByteArray =
        File(repoRoot(), "fixtures/circuit-relay/expected/$name.bin").readBytes()

    private fun unhex(s: String): ByteArray {
        if (s.isEmpty()) return ByteArray(0)
        return ByteArray(s.length / 2) { i ->
            ((Character.digit(s[i * 2], 16) shl 4) + Character.digit(s[i * 2 + 1], 16)).toByte()
        }
    }

    /** Payload rule: payload_len>0 → bytes[i]=(i%256); else hex-decode payload_hex. */
    private fun payloadFor(o: JSONObject): ByteArray {
        val len = o.optInt("payload_len", 0)
        if (len > 0) return ByteArray(len) { (it % 256).toByte() }
        return unhex(o.optString("payload_hex", ""))
    }

    /** connection_id: missing/empty → null (nil UUID on the wire); else parsed UUID. */
    private fun connIdOf(o: JSONObject): UUID? {
        val s = o.optString("connection_id", "")
        return if (s.isEmpty()) null else UUID.fromString(s)
    }

    private fun frameFromInput(o: JSONObject): RelayFrame = RelayFrame(
        type = RelayMessageType.fromByte(o.getInt("type"))
            ?: throw IllegalArgumentException("bad type ${o.getInt("type")}"),
        status = RelayStatus.fromByte(o.optInt("status", 0))
            ?: throw IllegalArgumentException("bad status ${o.optInt("status", 0)}"),
        sourceUhid = o.optString("source_uhid", ""),
        destinationUhid = o.optString("destination_uhid", ""),
        relayUhid = o.optString("relay_uhid", ""),
        connectionId = connIdOf(o),
        reservationExpiresAtMs = o.optLong("reservation_expires_at_ms", 0),
        limitDurationSeconds = o.optInt("limit_duration_seconds", 0),
        limitDataBytes = o.optLong("limit_data_bytes", 0),
        payload = payloadFor(o)
    )

    /** Every input case serialises byte-for-byte to its Go-oracle .bin vector. */
    @Test
    fun `relay frame serialises byte-identical to the Go oracle`() {
        val arr = inputs()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val name = o.getString("name")
            assertContentEquals(expected(name), RelayFrame.serialize(frameFromInput(o)), "$name: serialize byte mismatch")
        }
    }

    /** Every .bin vector deserialises back to the input fields. */
    @Test
    fun `relay frame deserialises every field round-trip`() {
        val arr = inputs()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val name = o.getString("name")
            val f = RelayFrame.deserialize(expected(name))

            assertEquals(o.getInt("type"), f.type.value.toInt(), "$name type")
            assertEquals(o.optInt("status", 0), f.status.value.toInt(), "$name status")
            assertEquals(o.optString("source_uhid", ""), f.sourceUhid, "$name source_uhid")
            assertEquals(o.optString("destination_uhid", ""), f.destinationUhid, "$name destination_uhid")
            assertEquals(o.optString("relay_uhid", ""), f.relayUhid, "$name relay_uhid")
            assertEquals(connIdOf(o), f.connectionId, "$name connection_id")
            assertEquals(o.optLong("reservation_expires_at_ms", 0), f.reservationExpiresAtMs, "$name reservation_expires_at_ms")
            assertEquals(o.optInt("limit_duration_seconds", 0), f.limitDurationSeconds, "$name limit_duration_seconds")
            assertEquals(o.optLong("limit_data_bytes", 0), f.limitDataBytes, "$name limit_data_bytes")
            assertContentEquals(payloadFor(o), f.payload, "$name payload")
        }
    }
}
