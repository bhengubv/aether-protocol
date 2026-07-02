// SPDX-License-Identifier: MIT
package aethernet.presence

import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.jupiter.api.Test
import java.io.File
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for the presence wire bindings ([PacketType.PresenceBeacon] 21 +
 * [PacketType.PresenceQuery] 22). Byte-identity gate (fixtures/presence/vectors.json —
 * both beacon vectors + the query vector) plus broadcast/query/handle behaviour. Uses the
 * in-memory [FakeMeshSender] — no transport needed. Mirrors the C#
 * PresenceEridAnnounceTests presence cases.
 */
class PresenceServiceTest {

    // ─── Fixture loading (shared fixtures/presence/vectors.json) ─────

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
        JSONObject(File(repoRoot(), "fixtures/presence/vectors.json").readText())

    // ─── Byte-identity gate (fixtures/presence/vectors.json) ─────
    // Beacon field order erid, geohash, capabilities, status, sent_at_ms; query field order
    // query_id, geohash. snake_case keys, capabilities/status/sent_at_ms bare integers,
    // lowercase-dashed UUID query_id, no whitespace. Byte-identical with C# in every port.

    @Test fun beacon_available_serializesToCanonicalBytes() {
        val v = vectors().getJSONArray("beacon_vectors").getJSONObject(0)
        val json = PresenceBeaconPayload(
            erid = "3B38HPPFG9JXE37Q",
            geohash = "u4pru",
            capabilities = 73,
            status = 1,
            sentAtMs = 1_700_000_000_000L,
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"erid\":\"3B38HPPFG9JXE37Q\",\"geohash\":\"u4pru\",\"capabilities\":73,\"status\":1,\"sent_at_ms\":1700000000000}",
            json,
        )
        assertEquals(v.getString("expected_json"), json, "beacon 'available' must match shared fixture")
    }

    @Test fun beacon_hiddenOffline_serializesToCanonicalBytes() {
        val v = vectors().getJSONArray("beacon_vectors").getJSONObject(1)
        val json = PresenceBeaconPayload(
            erid = "0Z5BD0HB1Q7W76MY",
            geohash = "",
            capabilities = 0,
            status = 5,
            sentAtMs = 0,
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"erid\":\"0Z5BD0HB1Q7W76MY\",\"geohash\":\"\",\"capabilities\":0,\"status\":5,\"sent_at_ms\":0}",
            json,
        )
        assertEquals(v.getString("expected_json"), json, "beacon 'hidden_offline' must match shared fixture")
    }

    @Test fun query_serializesToCanonicalBytes() {
        val v = vectors().getJSONArray("query_vectors").getJSONObject(0)
        val json = PresenceQueryPayload(
            queryId = UUID.fromString("11112222-3333-4444-5555-666677778888"),
            geohash = "u4pru",
        ).toJsonBytes().toString(Charsets.UTF_8)
        assertEquals(
            "{\"query_id\":\"11112222-3333-4444-5555-666677778888\",\"geohash\":\"u4pru\"}",
            json,
        )
        assertEquals(v.getString("expected_json"), json, "query must match shared fixture")
    }

    // ─── Broadcast + query + handle ─────────────────────────

    @Test fun broadcastBeacon_emitsBeaconPacket_andHandleRaisesEvent() = runBlocking {
        val sender = FakeMeshSender("aether:alice:01")
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:aa", identityKey = ByteArray(0)))
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:bb", identityKey = ByteArray(0)))
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:cc", identityKey = ByteArray(0)))
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:dd", identityKey = ByteArray(0)))
        val svc = PresenceService(sender)
        val beacon = PresenceBeaconPayload(
            erid = "3B38HPPFG9JXE37Q",
            geohash = "u4pru",
            capabilities = 73,
            status = 1,
            sentAtMs = 1_700_000_000_000L,
        )

        assertEquals(4, svc.broadcastBeacon(beacon))
        assertEquals(1, sender.broadcasts.size)
        val sent = sender.broadcasts[0]
        assertEquals(PacketType.PresenceBeacon, sent.type)

        var gotBeacon: PresenceBeaconPayload? = null
        var gotFrom: String? = null
        svc.onBeaconReceived = { b, from -> gotBeacon = b; gotFrom = from }
        sent.sourceUhid = "aether:alice:01"
        assertTrue(svc.handle(sent))
        assertNotNull(gotBeacon)
        assertEquals("3B38HPPFG9JXE37Q", gotBeacon!!.erid)
        assertEquals("aether:alice:01", gotFrom)
    }

    @Test fun query_emitsQueryPacket_andHandleRaisesEvent() = runBlocking {
        val sender = FakeMeshSender("aether:bob:02")
        val svc = PresenceService(sender)

        val qid = svc.query("u4pru")
        assertNotEquals(UUID(0L, 0L), qid)
        assertEquals(1, sender.broadcasts.size)
        val sent = sender.broadcasts[0]
        assertEquals(PacketType.PresenceQuery, sent.type)
        val body = PresenceQueryPayload.fromJson(sent.payload.toString(Charsets.UTF_8))!!
        assertEquals(qid, body.queryId)
        assertEquals("u4pru", body.geohash)

        var got: PresenceQueryPayload? = null
        svc.onQueryReceived = { q, _ -> got = q }
        assertTrue(svc.handle(sent))
        assertNotNull(got)
        assertEquals(qid, got!!.queryId)
    }

    @Test fun presence_handle_wrongType_returnsFalse() = runBlocking {
        val svc = PresenceService(FakeMeshSender("aether:local:01"))
        assertFalse(svc.handle(MeshPacket(type = PacketType.Data, payload = ByteArray(0))))
    }

    @Test fun presence_handle_beaconWithEmptyErid_returnsFalse() = runBlocking {
        val svc = PresenceService(FakeMeshSender("aether:local:01"))
        val pkt = MeshPacket(
            type = PacketType.PresenceBeacon,
            sourceUhid = "aether:x:01",
            payload = PresenceBeaconPayload(erid = "").toJsonBytes(),
        )
        assertFalse(svc.handle(pkt))
    }
}
