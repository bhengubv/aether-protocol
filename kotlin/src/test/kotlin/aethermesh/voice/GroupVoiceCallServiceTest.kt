// SPDX-License-Identifier: MIT
package aethermesh.voice

import aethermesh.FakeMeshSender
import aethermesh.protocol.MeshPacket
import aethermesh.protocol.PacketType
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

// ── Helpers ───────────────────────────────────────────────────────────────────

private fun makeSvc(uhid: String = "alice"): Pair<FakeMeshSender, GroupVoiceCallService> {
    val sender = FakeMeshSender(uhid)
    return sender to GroupVoiceCallService(sender)
}

private fun groupSignalingPacket(from: String, json: String): MeshPacket =
    MeshPacket(
        type = PacketType.VoiceSignaling,
        sourceUhid = from,
        payload = json.toByteArray(Charsets.UTF_8)
    )

private fun invitePacket(from: String, callId: UUID, invited: List<String>): MeshPacket {
    val arr = invited.joinToString(",") { "\"$it\"" }
    return groupSignalingPacket(
        from,
        """{"kind":"invite","call_id":"$callId","from_uhid":"$from","to_uhid":"alice","invited_uhids":[$arr],"key_generation":0}"""
    )
}

private fun joinPacket(from: String, callId: UUID): MeshPacket =
    groupSignalingPacket(
        from,
        """{"kind":"join","call_id":"$callId","from_uhid":"$from","to_uhid":"alice"}"""
    )

private fun leavePacket(from: String, callId: UUID): MeshPacket =
    groupSignalingPacket(
        from,
        """{"kind":"leave","call_id":"$callId","from_uhid":"$from","to_uhid":"alice"}"""
    )

private fun kickPacket(from: String, callId: UUID, kicked: String): MeshPacket =
    groupSignalingPacket(
        from,
        """{"kind":"kick","call_id":"$callId","from_uhid":"$from","to_uhid":"$kicked","kicked_uhid":"$kicked"}"""
    )

private fun keyRotationPacket(from: String, callId: UUID, keyGen: Int): MeshPacket =
    groupSignalingPacket(
        from,
        """{"kind":"key_rotation","call_id":"$callId","from_uhid":"$from","to_uhid":"alice","key_generation":$keyGen}"""
    )

private fun buildGroupFramePacket(from: String, callId: UUID, keyGen: Int = 0): MeshPacket {
    // [16] callId + [4] seq + [8] ts + [1] silence + [4] keyGen + [N] payload
    val audio = byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte(), 0xDD.toByte())
    val buf = ByteBuffer.allocate(16 + 4 + 8 + 1 + 4 + audio.size)
    buf.order(ByteOrder.BIG_ENDIAN)
    buf.putLong(callId.mostSignificantBits)
    buf.putLong(callId.leastSignificantBits)
    buf.order(ByteOrder.LITTLE_ENDIAN)
    buf.putInt(0)                           // sequence
    buf.putLong(System.currentTimeMillis()) // timestampMs
    buf.put(0)                              // isSilence=false
    buf.putInt(keyGen)                      // keyGeneration
    buf.put(audio)
    return MeshPacket(
        type = PacketType.VoiceCall,
        sourceUhid = from,
        payload = buf.array()
    )
}

// ── invite ────────────────────────────────────────────────────────────────────

class GroupVoiceCallServiceTest {

    @Test fun invite_sendsUnicastToEachMember() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        svc.invite(callId, listOf("bob", "carol"))

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        val toCarol = sender.unicasts.filter { it.nextHopUhid == "carol" }
        assertTrue(toBob.isNotEmpty(), "expected invite unicast to bob")
        assertTrue(toCarol.isNotEmpty(), "expected invite unicast to carol")

        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"invite\""), "expected kind=invite")
    }

    @Test fun invite_emptyMembers_throws() {
        val (_, svc) = makeSvc()
        var threw = false
        try {
            runBlocking { svc.invite(UUID.randomUUID(), emptyList()) }
        } catch (_: IllegalArgumentException) {
            threw = true
        }
        assertTrue(threw, "expected IllegalArgumentException for empty memberUhids")
    }

    // ── inbound invite ────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundInvite_firesOnInvited() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        var session: GroupVoiceCallSession? = null
        svc.onInvited = { session = it }

        svc.handlePacket(invitePacket("bob", callId, listOf("alice", "carol")))

        assertTrue(session != null, "onInvited not fired")
        assertEquals(GroupVoiceCallState.Invited, session?.state)
        assertEquals("bob", session?.hostUhid)
        assertEquals(callId, session?.id)
    }

    // ── inbound join ──────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundJoin_firesOnMembershipChanged() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        // Alice is host; create session by inviting.
        svc.invite(callId, listOf("bob"))

        var changedSession: GroupVoiceCallSession? = null
        svc.onMembershipChanged = { changedSession = it }

        svc.handlePacket(joinPacket("carol", callId))

        assertTrue(changedSession != null, "onMembershipChanged not fired on join")
        assertTrue(changedSession!!.members.contains("carol"), "carol should be in members after join")
    }

    // ── inbound leave ─────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundLeave_removesMemberAndFires() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        svc.invite(callId, listOf("bob", "carol"))

        var changedSession: GroupVoiceCallSession? = null
        svc.onMembershipChanged = { changedSession = it }

        svc.handlePacket(leavePacket("bob", callId))

        assertTrue(changedSession != null, "onMembershipChanged not fired on leave")
        assertFalse(changedSession!!.members.contains("bob"), "bob should be removed after leave")
    }

    // ── inbound kick ──────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundKick_removesMemberAndFires() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        svc.invite(callId, listOf("bob", "carol"))

        var changedSession: GroupVoiceCallSession? = null
        svc.onMembershipChanged = { changedSession = it }

        // Kick message received (alice is not the kicked one, bob is)
        svc.handlePacket(kickPacket("alice", callId, "bob"))

        assertTrue(changedSession != null, "onMembershipChanged not fired on kick")
        assertFalse(changedSession!!.members.contains("bob"), "bob should be removed after kick")
    }

    @Test fun handlePacket_kickedSelf_sessionEnded() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        // Alice receives invite (state=Invited)
        svc.handlePacket(invitePacket("bob", callId, listOf("alice", "carol")))

        var changedSession: GroupVoiceCallSession? = null
        svc.onMembershipChanged = { changedSession = it }

        // Alice is kicked
        svc.handlePacket(kickPacket("bob", callId, "alice"))

        assertTrue(changedSession != null, "onMembershipChanged not fired when self kicked")
        assertEquals(GroupVoiceCallState.Ended, changedSession?.state)
    }

    // ── key rotation ──────────────────────────────────────────────────────────

    @Test fun handlePacket_keyRotation_firesOnKeyRotation() = runBlocking {
        val (_, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        // Alice is invited
        svc.handlePacket(invitePacket("bob", callId, listOf("alice")))

        var gotCallId: UUID? = null
        var gotKeyGen: UInt? = null
        svc.onKeyRotation = { cid, kg -> gotCallId = cid; gotKeyGen = kg }

        svc.handlePacket(keyRotationPacket("bob", callId, 1))

        assertTrue(gotCallId != null, "onKeyRotation not fired")
        assertEquals(callId, gotCallId)
        assertEquals(1u, gotKeyGen)
    }

    // ── host kick (outbound) ──────────────────────────────────────────────────

    @Test fun kick_sendsKickUnicastToTargetAndFiresMembershipChanged() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        svc.invite(callId, listOf("bob", "carol"))
        sender.clear()

        var changedSession: GroupVoiceCallSession? = null
        svc.onMembershipChanged = { changedSession = it }

        svc.kick(callId, "bob")

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        assertTrue(toBob.isNotEmpty(), "expected kick unicast to bob")
        val json = String(toBob[0].packet.payload, Charsets.UTF_8)
        assertTrue(json.contains("\"kick\""), "expected kind=kick in payload")

        assertTrue(changedSession != null, "onMembershipChanged not fired after kick")
        assertFalse(changedSession!!.members.contains("bob"), "bob must be removed after kick")
    }

    // ── sendFrame ─────────────────────────────────────────────────────────────

    @Test fun sendFrame_fansOutToAllMembersExceptSelf() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        svc.invite(callId, listOf("bob", "carol"))
        sender.clear()

        svc.sendFrame(callId, byteArrayOf(1, 2, 3), isSilence = false, keyGeneration = 0u)

        val toBob = sender.unicasts.filter { it.nextHopUhid == "bob" }
        val toCarol = sender.unicasts.filter { it.nextHopUhid == "carol" }
        val toAlice = sender.unicasts.filter { it.nextHopUhid == "alice" }
        assertTrue(toBob.isNotEmpty(), "bob should receive frame")
        assertTrue(toCarol.isNotEmpty(), "carol should receive frame")
        assertEquals(0, toAlice.size, "alice (self) must not receive frame")

        assertEquals(PacketType.VoiceCall, toBob[0].packet.type)
    }

    @Test fun sendFrame_notActive_noPacketSent() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        // Receive invite → state=Invited, not Active
        svc.handlePacket(invitePacket("bob", callId, listOf("alice")))
        sender.clear()

        svc.sendFrame(callId, byteArrayOf(1, 2, 3), isSilence = false, keyGeneration = 0u)

        assertEquals(0, sender.unicasts.size, "no frame should be sent while in Invited state")
    }

    // ── inbound frame ─────────────────────────────────────────────────────────

    @Test fun handlePacket_inboundFrame_firesOnFrameReceived() = runBlocking {
        val (sender, svc) = makeSvc("alice")
        val callId = UUID.randomUUID()

        svc.invite(callId, listOf("bob"))
        sender.clear()

        var gotCallId: UUID? = null
        var gotFrom: String? = null
        var gotPayload: ByteArray? = null
        svc.onFrameReceived = { cid, from, audio, _, _ ->
            gotCallId = cid
            gotFrom = from
            gotPayload = audio
        }

        svc.handlePacket(buildGroupFramePacket("bob", callId))

        assertTrue(gotPayload != null, "onFrameReceived was not fired")
        assertEquals(callId, gotCallId)
        assertEquals("bob", gotFrom)
    }
}
