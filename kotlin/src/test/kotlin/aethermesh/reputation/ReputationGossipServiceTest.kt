// SPDX-License-Identifier: MIT

package aethermesh.reputation

import aethermesh.security.NodeReputationService
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Test
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

private const val LOCAL_UHID   = "local-node"
private const val REPORTER     = "reporter-node"
private const val TARGET       = "target-node"
private const val EPSILON      = 1e-9

// ── Fake implementations ────────────────────────────────────────────────────

private class FakeSender(
    override val localUhid: String = LOCAL_UHID,
) : ReputationGossipService.MeshSender {

    private val _packets: MutableList<ReputationGossipService.GossipPacket> = mutableListOf()
    val packets: List<ReputationGossipService.GossipPacket> get() = _packets.toList()

    override fun broadcast(packet: ReputationGossipService.GossipPacket): Int {
        _packets.add(packet)
        return 1
    }
}

private class FakeSigner(private val verifyOk: Boolean = true) : ReputationGossipService.PacketSigner {

    override fun sign(packet: ReputationGossipService.GossipPacket) {
        packet.signature   = ByteArray(64)
        packet.packetNonce = ByteArray(8)
    }

    override fun verify(
        packet: ReputationGossipService.GossipPacket,
        senderPublicKey: ByteArray,
    ): Boolean = verifyOk
}

// ── Helper ──────────────────────────────────────────────────────────────────

private fun makeValidPacket(
    reporter: String  = REPORTER,
    target: String    = TARGET,
    delta: Double     = -0.20,
    tsMs: Long        = System.currentTimeMillis(),
    reason: String    = "test",
): ReputationGossipService.GossipPacket {
    val payload = ReputationGossipService.ReputationUpdatePayload(
        reporterUhid = reporter,
        targetUhid   = target,
        scoreDelta   = delta,
        timestampMs  = tsMs,
        reason       = reason,
    )
    val bytes = Json.encodeToString(payload).toByteArray(Charsets.UTF_8)
    return ReputationGossipService.GossipPacket(
        packetType      = 52.toByte(),   // PacketType.ReputationUpdate.value
        sourceUhid      = reporter,
        destinationUhid = "*",
        ttl             = 3,
        payload         = bytes,
        timestampMs     = tsMs,
        signature       = ByteArray(64),
        packetNonce     = ByteArray(8),
    )
}

private fun pubKey() = ByteArray(32)

// ── Tests ────────────────────────────────────────────────────────────────────

class ReputationGossipServiceTest {

    // 1. broadcastSendsOnePacket
    @Test fun `broadcastSendsOnePacket`() {
        val sender = FakeSender()
        val svc = ReputationGossipService(sender, FakeSigner(), NodeReputationService())
        val fanOut = svc.broadcastReputationUpdate(TARGET, -0.20, "bad-routing")
        assertEquals(1, fanOut)
        assertEquals(1, sender.packets.size)
    }

    // 2. broadcastPayloadFields
    @Test fun `broadcastPayloadFields`() {
        val sender = FakeSender()
        val svc = ReputationGossipService(sender, FakeSigner(), NodeReputationService())
        val before = System.currentTimeMillis()
        svc.broadcastReputationUpdate(TARGET, -0.20, "reason-x")
        val after = System.currentTimeMillis()

        val pkt = sender.packets.single()
        val json = pkt.payload.toString(Charsets.UTF_8)
        val payload = Json.decodeFromString<ReputationGossipService.ReputationUpdatePayload>(json)

        assertEquals(LOCAL_UHID, payload.reporterUhid)
        assertEquals(TARGET, payload.targetUhid)
        assertEquals(-0.20, payload.scoreDelta, EPSILON)
        assertEquals("reason-x", payload.reason)
        assertTrue(payload.timestampMs in before..after)
    }

    // 3. broadcastClampsAboveOne
    @Test fun `broadcastClampsAboveOne`() {
        val sender = FakeSender()
        val svc = ReputationGossipService(sender, FakeSigner(), NodeReputationService())
        svc.broadcastReputationUpdate(TARGET, 5.0, "spike")

        val json = sender.packets.single().payload.toString(Charsets.UTF_8)
        val payload = Json.decodeFromString<ReputationGossipService.ReputationUpdatePayload>(json)
        assertEquals(1.0, payload.scoreDelta, EPSILON)
    }

    // 4. broadcastClampsBelowMinusOne
    @Test fun `broadcastClampsBelowMinusOne`() {
        val sender = FakeSender()
        val svc = ReputationGossipService(sender, FakeSigner(), NodeReputationService())
        svc.broadcastReputationUpdate(TARGET, -9.0, "flood")

        val json = sender.packets.single().payload.toString(Charsets.UTF_8)
        val payload = Json.decodeFromString<ReputationGossipService.ReputationUpdatePayload>(json)
        assertEquals(-1.0, payload.scoreDelta, EPSILON)
    }

    // 5. handleInvalidSignature
    @Test fun `handleInvalidSignature`() {
        val rep = NodeReputationService()
        val svc = ReputationGossipService(FakeSender(), FakeSigner(verifyOk = false), rep)
        val accepted = svc.handleGossipPacket(makeValidPacket(), pubKey())
        assertFalse(accepted)
        assertEquals(1.0, rep.getReputationScore(TARGET), EPSILON) // unchanged
    }

    // 6. handleWrongType
    @Test fun `handleWrongType`() {
        val rep = NodeReputationService()
        val svc = ReputationGossipService(FakeSender(), FakeSigner(), rep)
        val pkt = makeValidPacket().copy(packetType = 99.toByte())
        val accepted = svc.handleGossipPacket(pkt, pubKey())
        assertFalse(accepted)
        assertEquals(1.0, rep.getReputationScore(TARGET), EPSILON)
    }

    // 7. handleStaleTimestamp
    @Test fun `handleStaleTimestamp`() {
        val rep = NodeReputationService()
        val svc = ReputationGossipService(FakeSender(), FakeSigner(), rep)
        val staleMs = System.currentTimeMillis() - (6 * 60 * 1000L) // 6 minutes ago
        val pkt = makeValidPacket(tsMs = staleMs)
        val accepted = svc.handleGossipPacket(pkt, pubKey())
        assertFalse(accepted)
        assertEquals(1.0, rep.getReputationScore(TARGET), EPSILON)
    }

    // 8. handleMissingReporterUhid
    @Test fun `handleMissingReporterUhid`() {
        val rep = NodeReputationService()
        val svc = ReputationGossipService(FakeSender(), FakeSigner(), rep)
        val pkt = makeValidPacket(reporter = "")
        val accepted = svc.handleGossipPacket(pkt, pubKey())
        assertFalse(accepted)
        assertEquals(1.0, rep.getReputationScore(TARGET), EPSILON)
    }

    // 9. handleOwnGossip
    @Test fun `handleOwnGossip`() {
        val rep = NodeReputationService()
        val svc = ReputationGossipService(FakeSender(LOCAL_UHID), FakeSigner(), rep)
        // Reporter == localUhid — own echo
        val pkt = makeValidPacket(reporter = LOCAL_UHID)
        val accepted = svc.handleGossipPacket(pkt, pubKey())
        assertFalse(accepted)
        assertEquals(1.0, rep.getReputationScore(TARGET), EPSILON)
    }

    // 10. handleUnknownReporterFullDelta
    //     unknown reporter → score 1.0 → effective = −0.20 × 1.0 = −0.20 → target 0.80
    @Test fun `handleUnknownReporterFullDelta`() {
        val rep = NodeReputationService()
        val svc = ReputationGossipService(FakeSender(), FakeSigner(), rep)
        val pkt = makeValidPacket(reporter = REPORTER, target = TARGET, delta = -0.20)
        val accepted = svc.handleGossipPacket(pkt, pubKey())
        assertTrue(accepted)
        assertTrue(abs(rep.getReputationScore(TARGET) - 0.80) < EPSILON)
    }

    // 11. handleDegradedReporterWeightedDelta
    //     degrade reporter to 0.50 (10× recordRreqFloodAttempt −0.05 each)
    //     effective = −0.20 × 0.50 = −0.10 → target 0.90
    @Test fun `handleDegradedReporterWeightedDelta`() {
        val rep = NodeReputationService()
        repeat(10) { rep.recordRreqFloodAttempt(REPORTER) } // 1.0 − 0.50 = 0.50
        val svc = ReputationGossipService(FakeSender(), FakeSigner(), rep)
        val pkt = makeValidPacket(reporter = REPORTER, target = TARGET, delta = -0.20)
        val accepted = svc.handleGossipPacket(pkt, pubKey())
        assertTrue(accepted)
        assertTrue(abs(rep.getReputationScore(TARGET) - 0.90) < EPSILON)
    }

    // 12. handlePositiveDeltaImprovesTarget
    //     pre-degrade target with recordSignatureFailure → 0.80
    //     positive delta +0.10 × 1.0 = +0.10 → target 0.90
    @Test fun `handlePositiveDeltaImprovesTarget`() {
        val rep = NodeReputationService()
        rep.recordSignatureFailure(TARGET)  // 1.0 − 0.20 = 0.80
        val svc = ReputationGossipService(FakeSender(), FakeSigner(), rep)
        val pkt = makeValidPacket(reporter = REPORTER, target = TARGET, delta = +0.10)
        val accepted = svc.handleGossipPacket(pkt, pubKey())
        assertTrue(accepted)
        assertTrue(abs(rep.getReputationScore(TARGET) - 0.90) < EPSILON)
    }
}
