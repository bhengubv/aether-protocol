// SPDX-License-Identifier: MIT

package aethernet.sync

import org.json.JSONArray
import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import java.util.UUID
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Cross-language multi-device-sync parity: the Kotlin port must reproduce the
 * shared vectors in fixtures/sync/vectors.json byte-for-byte — the same three
 * components every AetherNet SDK and the C# reference reproduce.
 *
 *  - SyncRecord   : hex(serialize(rec)) == serialized_hex, plus deserialize round-trip.
 *  - Reconcile    : winner(records).recordId == winner_record_id, order-independent.
 *  - DeviceLink   : signed-body hex, deterministic signature hex, serialize hex,
 *                   verify(identity) == true / verify(wrong) == false, round-trip.
 *
 * Uses org.json for parsing (the Soong-compatible manual-decode path already used
 * by DtnEnvelopeFixtureTest) — no kotlinx.serialization.
 */
class SyncFixtureTest {

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
        JSONObject(File(repoRoot(), "fixtures/sync/vectors.json").readText())

    private fun unhex(s: String): ByteArray {
        if (s.isEmpty()) return ByteArray(0)
        return ByteArray(s.length / 2) { i ->
            ((Character.digit(s[i * 2], 16) shl 4) + Character.digit(s[i * 2 + 1], 16)).toByte()
        }
    }

    private fun hex(b: ByteArray): String =
        b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    private fun recordFrom(o: JSONObject): SyncRecord = SyncRecord(
        recordId = UUID.fromString(o.getString("record_id")),
        deviceId = o.getString("device_id"),
        op = SyncOp.fromCode(o.getInt("op")),
        itemId = o.getString("item_id"),
        logicalClock = o.getLong("logical_clock"),
        createdAtMs = o.getLong("created_at_ms"),
        encryptedPayload = unhex(o.optString("payload_hex", "")),
    )

    // ─── SyncRecord: canonical bytes + round-trip ───────────────────────────

    @Test
    fun `sync records serialise byte-identical and round-trip`() {
        val arr = vectors().getJSONArray("sync_records")
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val rec = recordFrom(o)
            val expectedHex = o.getString("serialized_hex")

            // serialize == fixture hex
            val bytes = SyncRecordSerializer.serialize(rec)
            assertEquals(expectedHex, hex(bytes), "serialize mismatch for record_id ${o.getString("record_id")}")

            // deserialize(fixture bytes) == rec  (parse the canonical bytes, not our own)
            val decoded = SyncRecordSerializer.deserialize(unhex(expectedHex))
            assertEquals(rec, decoded, "deserialize round-trip mismatch for ${o.getString("record_id")}")

            // and deserialize(serialize(rec)) == rec
            assertEquals(rec, SyncRecordSerializer.deserialize(bytes), "serialize->deserialize mismatch")
        }
    }

    // ─── Reconcile: deterministic LWW, order-independent ────────────────────

    @Test
    fun `reconcile picks the deterministic last-write-wins winner in any order`() {
        val arr = vectors().getJSONArray("reconcile")
        for (i in 0 until arr.length()) {
            val group = arr.getJSONObject(i)
            val name = group.getString("name")
            val recArr = group.getJSONArray("records")
            val records = (0 until recArr.length()).map { recordFrom(recArr.getJSONObject(it)) }
            val expected = UUID.fromString(group.getString("winner_record_id"))

            // forward order
            assertEquals(expected, SyncReconciler.winner(records).recordId, "$name winner (forward)")
            // reversed order — winner must be order-independent
            assertEquals(expected, SyncReconciler.winner(records.reversed()).recordId, "$name winner (reversed)")

            // merge keeps one winner per item; every record here shares item "x"
            val merged = SyncReconciler.merge(records)
            assertEquals(1, merged.size, "$name merge should collapse to one item")
            assertEquals(expected, merged.values.first().recordId, "$name merge winner")
        }
    }

    // ─── DeviceLink: body, signature, serialize, verify, round-trip ─────────

    @Test
    fun `device links reproduce signed body signature and verify correctly`() {
        val root = vectors()
        val identityPrivate = unhex(root.getString("identity_private"))
        val identityPublic = unhex(root.getString("identity_public"))
        val wrongPublic = unhex(root.getString("wrong_identity_public"))

        val arr = root.getJSONArray("device_links")
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            val deviceId = o.getString("device_id")
            val devicePublic = unhex(o.getString("device_public_key"))
            val issuedAtMs = o.getLong("issued_at_ms")

            // signed body == fixture
            val body = DeviceLinkCodec.signedBody(deviceId, devicePublic, issuedAtMs)
            assertEquals(o.getString("signed_body_hex"), hex(body), "$deviceId signed_body")

            // deterministic Ed25519 signature == fixture
            val link = DeviceLinkCodec.create(deviceId, devicePublic, issuedAtMs, identityPrivate)
            assertEquals(o.getString("signature_hex"), hex(link.signature), "$deviceId signature")

            // full serialize == fixture (body || signature)
            val serialized = DeviceLinkCodec.serialize(link)
            assertEquals(o.getString("serialized_hex"), hex(serialized), "$deviceId serialize")

            // verify: right identity true, wrong identity false
            assertTrue(DeviceLinkCodec.verify(link, identityPublic), "$deviceId verify(identity) must be true")
            assertFalse(DeviceLinkCodec.verify(link, wrongPublic), "$deviceId verify(wrong) must be false")

            // deserialize round-trip == the created link
            val decoded = DeviceLinkCodec.deserialize(serialized)
            assertEquals(deviceId, decoded.deviceId, "$deviceId round-trip deviceId")
            assertContentEquals(devicePublic, decoded.devicePublicKey, "$deviceId round-trip devicePublicKey")
            assertEquals(issuedAtMs, decoded.issuedAtMs, "$deviceId round-trip issuedAtMs")
            assertContentEquals(link.signature, decoded.signature, "$deviceId round-trip signature")
            assertEquals(link, decoded, "$deviceId round-trip full equality")
            assertTrue(DeviceLinkCodec.verify(decoded, identityPublic), "$deviceId decoded verify must be true")
        }
    }
}
