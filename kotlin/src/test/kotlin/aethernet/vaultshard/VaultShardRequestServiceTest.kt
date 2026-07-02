// SPDX-License-Identifier: MIT
package aethernet.vaultshard

import aethernet.FakeMeshSender
import aethernet.protocol.MeshPacket
import aethernet.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Unit tests for the VaultShardRequest wire binding ([PacketType.VaultShardRequest], 42).
 * Byte-identity gate (fixtures/vaultshard/vectors.json) + request/handle behaviour.
 * Uses the in-memory [FakeMeshSender] — no transport needed. Mirrors the C#
 * WirePacketsTests VaultShardRequest cases.
 */
class VaultShardRequestServiceTest {

    // ─── Byte-identity gate (fixtures/vaultshard/vectors.json) ─────
    // Field order shard_hash, requester_uhid. snake_case, no whitespace. Byte-identical with C#.

    @Test fun vaultShardRequest_serializesToCanonicalBytes() {
        val json = VaultShardRequest(shardHash = "QmShardHash789", requesterUhid = "aether:bob:02")
            .toJsonBytes().toString(Charsets.UTF_8)
        assertEquals("{\"shard_hash\":\"QmShardHash789\",\"requester_uhid\":\"aether:bob:02\"}", json)
    }

    // ─── Request + handle ───────────────────────────────────

    @Test fun vault_request_emitsShardRequestPacket_andHandleRaisesEvent() = runBlocking {
        val sender = FakeMeshSender("aether:bob:02")
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:aa", identityKey = ByteArray(0)))
        sender.addPeer(aethernet.models.PeerInfo(uhid = "aether:peer:bb", identityKey = ByteArray(0)))
        val svc = VaultShardRequestService(sender)

        val reached = svc.requestShard("QmShardHash789")
        assertEquals(2, reached)
        assertEquals(1, sender.broadcasts.size)
        val sent = sender.broadcasts[0]
        assertEquals(PacketType.VaultShardRequest, sent.type)

        val body = VaultShardRequest.fromJson(sent.payload.toString(Charsets.UTF_8))!!
        assertEquals("QmShardHash789", body.shardHash)
        assertEquals("aether:bob:02", body.requesterUhid)

        var got: VaultShardRequest? = null
        svc.onShardRequested = { got = it }
        assertTrue(svc.handle(sent))
        assertNotNull(got)
        assertEquals("QmShardHash789", got!!.shardHash)
        assertEquals("aether:bob:02", got!!.requesterUhid)
    }

    @Test fun vault_handle_wrongType_returnsFalse() = runBlocking {
        val svc = VaultShardRequestService(FakeMeshSender("aether:local:01"))
        assertFalse(svc.handle(MeshPacket(type = PacketType.Data, payload = ByteArray(0))))
    }
}
