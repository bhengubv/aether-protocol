// SPDX-License-Identifier: MIT

package aethernet.security

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

private const val ALICE = "alice-uhid"
private const val BOB   = "bob-uhid"
private const val DELTA = 1e-9

class NodeReputationServiceTest {

    private fun newSvc() = NodeReputationService()

    // ── Default score ────────────────────────────────────────────────────────

    @Test fun `unknown peer returns 1_0`() {
        val svc = newSvc()
        assertEquals(1.0, svc.getReputationScore("nobody"), DELTA)
    }

    // ── Negative signals ─────────────────────────────────────────────────────

    @Test fun `rreq flood reduces score to 0_95`() {
        val svc = newSvc()
        svc.recordRreqFloodAttempt(ALICE)
        assertEquals(0.95, svc.getReputationScore(ALICE), DELTA)
    }

    @Test fun `replay attempt reduces score to 0_85`() {
        val svc = newSvc()
        svc.recordReplayAttempt(ALICE)
        assertEquals(0.85, svc.getReputationScore(ALICE), DELTA)
    }

    @Test fun `signature failure reduces score to 0_80`() {
        val svc = newSvc()
        svc.recordSignatureFailure(ALICE)
        assertEquals(0.80, svc.getReputationScore(ALICE), DELTA)
    }

    @Test fun `custody refusal reduces score to 0_95`() {
        val svc = newSvc()
        svc.recordCustodyRefusal(ALICE)
        assertEquals(0.95, svc.getReputationScore(ALICE), DELTA)
    }

    @Test fun `delivery failure reduces score to 0_98`() {
        val svc = newSvc()
        svc.recordDeliveryFailure(ALICE)
        assertEquals(0.98, svc.getReputationScore(ALICE), DELTA)
    }

    // ── Clamping ─────────────────────────────────────────────────────────────

    @Test fun `5x signature failure clamps to 0_0`() {
        val svc = newSvc()
        // 5 × −0.20 = −1.0 → floor at 0.0 (epsilon-snapped)
        repeat(5) { svc.recordSignatureFailure(ALICE) }
        assertEquals(0.0, svc.getReputationScore(ALICE), DELTA)
    }

    @Test fun `10x delivery success clamps to 1_0`() {
        val svc = newSvc()
        // already at 1.0; 10 × +0.01 would overflow → still 1.0
        repeat(10) { svc.recordDeliverySuccess(ALICE, roundTripMs = 50) }
        assertEquals(1.0, svc.getReputationScore(ALICE), DELTA)
    }

    // ── No cross-contamination ────────────────────────────────────────────────

    @Test fun `signals do not cross-contaminate peers`() {
        val svc = newSvc()
        svc.recordSignatureFailure(ALICE)
        svc.recordSignatureFailure(ALICE)

        val aliceScore = svc.getReputationScore(ALICE)
        val bobScore   = svc.getReputationScore(BOB)

        assertTrue(aliceScore < 1.0, "Alice should have a reduced score")
        assertEquals(1.0, bobScore, DELTA) // Bob untouched
    }

    // ── GetAllScores snapshot ─────────────────────────────────────────────────

    @Test fun `getAllScores returns snapshot of all known peers`() {
        val svc = newSvc()
        svc.recordRreqFloodAttempt(ALICE)
        svc.recordReplayAttempt(BOB)

        val all = svc.getAllScores()
        assertEquals(2, all.size)
        assertTrue(all.containsKey(ALICE))
        assertTrue(all.containsKey(BOB))
        assertTrue(all[ALICE]!! < 1.0)
        assertTrue(all[BOB]!! < 1.0)
    }

    @Test fun `getAllScores snapshot is isolated from subsequent changes`() {
        val svc = newSvc()
        svc.recordRreqFloodAttempt(ALICE)
        val snapshot = svc.getAllScores()

        // Mutate after snapshot
        svc.recordSignatureFailure(ALICE)

        // snapshot must be unchanged
        assertEquals(0.95, snapshot[ALICE]!!, DELTA)
        // live score must reflect the new event
        assertEquals(0.75, svc.getReputationScore(ALICE), DELTA)
    }

    @Test fun `getAllScores returns empty map when no signals recorded`() {
        val svc = newSvc()
        assertTrue(svc.getAllScores().isEmpty())
    }

    // ── Compound signals ──────────────────────────────────────────────────────

    @Test fun `compound signals accumulate to 0_60`() {
        val svc = newSvc()
        svc.recordRreqFloodAttempt(ALICE)  // −0.05 → 0.95
        svc.recordReplayAttempt(ALICE)     // −0.15 → 0.80
        svc.recordSignatureFailure(ALICE)  // −0.20 → 0.60

        assertEquals(0.60, svc.getReputationScore(ALICE), DELTA)
    }

    @Test fun `recovery signals partially restore score`() {
        val svc = newSvc()
        svc.recordSignatureFailure(ALICE) // 0.80
        svc.recordSignatureFailure(ALICE) // 0.60
        svc.recordDeliverySuccess(ALICE, roundTripMs = 100) // 0.61
        svc.recordDeliverySuccess(ALICE, roundTripMs = 100) // 0.62

        assertEquals(0.62, svc.getReputationScore(ALICE), DELTA)
    }

    // ── Thread safety ─────────────────────────────────────────────────────────

    @Test fun `concurrent updates do not corrupt score`() {
        val svc = newSvc()
        val threads = (1..20).map {
            Thread { svc.recordDeliverySuccess(ALICE, roundTripMs = 10) }
        }
        threads.forEach { it.start() }
        threads.forEach { it.join() }

        // Score should be clamped at 1.0 — no NaN / negative from race
        val score = svc.getReputationScore(ALICE)
        assertFalse(score.isNaN(), "Score must not be NaN after concurrent updates")
        assertTrue(score in 0.0..1.0, "Score must be in [0.0, 1.0]")
    }

    // ── getAllScores does not include unknown-peer default ────────────────────

    @Test fun `getAllScores does not include peers queried but never signalled`() {
        val svc = newSvc()
        // Querying an unknown peer must not insert it into the map
        svc.getReputationScore("ghost")
        assertTrue(svc.getAllScores().isEmpty())
    }
}
