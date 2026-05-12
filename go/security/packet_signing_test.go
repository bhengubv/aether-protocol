// SPDX-License-Identifier: MIT

package security

import (
	"testing"

	"github.com/thegeeknetwork/aether-protocol-go/reputation"
)

// ── helpers ───────────────────────────────────────────────────────────────────

func newTestService() *PacketSigningService {
	return NewPacketSigningService(300) // 5-minute TTL
}

func nonce(b ...byte) []byte { return b }

// ── IsNonceSeen ───────────────────────────────────────────────────────────────

func TestPacketSigning_IsNonceSeen_FalseBeforeAnyRecord(t *testing.T) {
	svc := newTestService()
	defer svc.Close()
	if svc.IsNonceSeen("alice", nonce(1, 2, 3, 4, 5, 6, 7, 8)) {
		t.Error("expected false for a nonce that has never been recorded")
	}
}

func TestPacketSigning_IsNonceSeen_TrueAfterRecord(t *testing.T) {
	svc := newTestService()
	defer svc.Close()
	n := nonce(0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44)
	svc.RecordNonce("bob", n)
	if !svc.IsNonceSeen("bob", n) {
		t.Error("expected true after recording nonce")
	}
}

// ── Replay detection ─────────────────────────────────────────────────────────

func TestPacketSigning_SameSourceSameNonce_IsReplay(t *testing.T) {
	svc := newTestService()
	defer svc.Close()
	n := nonce(1, 2, 3, 4, 5, 6, 7, 8)

	svc.RecordNonce("carol", n)

	if !svc.IsNonceSeen("carol", n) {
		t.Fatal("expected replay to be detected")
	}
}

func TestPacketSigning_DifferentSourceSameNonceBytes_NotReplay(t *testing.T) {
	// Two senders may legitimately use the same nonce bytes.
	// The dedup key is (source, nonce), not nonce alone.
	svc := newTestService()
	defer svc.Close()
	n := nonce(0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03)

	svc.RecordNonce("alice", n)

	if svc.IsNonceSeen("bob", n) {
		t.Error("different source must not be flagged as replay")
	}
}

func TestPacketSigning_SameSourceDifferentNonce_NotReplay(t *testing.T) {
	svc := newTestService()
	defer svc.Close()
	svc.RecordNonce("dave", nonce(1, 1, 1, 1, 1, 1, 1, 1))
	if svc.IsNonceSeen("dave", nonce(2, 2, 2, 2, 2, 2, 2, 2)) {
		t.Error("different nonce from same source must not be flagged as replay")
	}
}

func TestPacketSigning_PreRegisteredNonce_DoesNotBlockLegitSender(t *testing.T) {
	// An attacker that pre-records the nonce key for a victim source cannot
	// block the victim, because the dedup key includes the source UHID.
	svc := newTestService()
	defer svc.Close()
	victimNonce := nonce(0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x01)

	// Attacker registers a nonce under its own identity (or some other source).
	svc.RecordNonce("attacker", victimNonce)

	// The victim's packet with the same nonce bytes must still be fresh.
	if svc.IsNonceSeen("victim", victimNonce) {
		t.Error("attacker pre-registration must not block legitimate victim sender")
	}
}

// ── ComputeSignableData ───────────────────────────────────────────────────────

func TestPacketSigning_ComputeSignableData_Deterministic(t *testing.T) {
	svc := newTestService()
	defer svc.Close()

	n := nonce(0, 1, 2, 3, 4, 5, 6, 7)
	a := svc.ComputeSignableData(n, 1_000_000, 2, "alice", "bob", []byte("hello"), 7, 0)
	b := svc.ComputeSignableData(n, 1_000_000, 2, "alice", "bob", []byte("hello"), 7, 0)
	if len(a) != len(b) {
		t.Fatalf("lengths differ: %d vs %d", len(a), len(b))
	}
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("bytes differ at position %d", i)
		}
	}
}

func TestPacketSigning_ComputeSignableData_DifferentNonce_DifferentOutput(t *testing.T) {
	svc := newTestService()
	defer svc.Close()
	a := svc.ComputeSignableData(nonce(0, 0, 0, 0, 0, 0, 0, 1), 0, 0, "x", "y", nil, 0, 0)
	b := svc.ComputeSignableData(nonce(0, 0, 0, 0, 0, 0, 0, 2), 0, 0, "x", "y", nil, 0, 0)
	same := true
	for i := range a {
		if a[i] != b[i] {
			same = false
			break
		}
	}
	if same {
		t.Error("different nonces must produce different signable data")
	}
}

func TestPacketSigning_ComputeSignableData_DifferentPayload_DifferentOutput(t *testing.T) {
	svc := newTestService()
	defer svc.Close()
	n := nonce(1, 2, 3, 4, 5, 6, 7, 8)
	a := svc.ComputeSignableData(n, 0, 0, "x", "y", []byte("payload-A"), 0, 0)
	b := svc.ComputeSignableData(n, 0, 0, "x", "y", []byte("payload-B"), 0, 0)
	same := true
	for i := range a {
		if a[i] != b[i] {
			same = false
			break
		}
	}
	if same {
		t.Error("different payloads must produce different signable data")
	}
}

func TestPacketSigning_ComputeSignableData_MinimumLength(t *testing.T) {
	svc := newTestService()
	defer svc.Close()
	// nonce(8) + ts(8) + type(4) + srcLen(4) + src(0) + dstLen(4) + dst(0) +
	// blake3(32) + ttl(4) + priority(4) = 68 bytes minimum
	result := svc.ComputeSignableData(nonce(0, 0, 0, 0, 0, 0, 0, 0), 0, 0, "", "", nil, 0, 0)
	if len(result) < 68 {
		t.Errorf("signable data too short: got %d, want >= 68", len(result))
	}
}

// ── ValidateAndRecordNonce ────────────────────────────────────────────────────

// TestValidateAndRecordNonce_Replay_FiresReputation verifies that a duplicate
// (sourceUhid, nonce) pair fires RecordReplayAttempt and returns false.
func TestValidateAndRecordNonce_Replay_FiresReputation(t *testing.T) {
	svc := newTestService()
	defer svc.Close()

	rep := reputation.NewNodeReputationService()
	svc.SetReputation(rep)

	n := nonce(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08)
	const uhid = "eve"

	// First call — must be accepted and recorded.
	if !svc.ValidateAndRecordNonce(uhid, n) {
		t.Fatal("first ValidateAndRecordNonce must return true for a fresh nonce")
	}

	scoreBefore := rep.GetReputationScore(uhid)

	// Second call with the same nonce — must be rejected and penalised.
	if svc.ValidateAndRecordNonce(uhid, n) {
		t.Fatal("second ValidateAndRecordNonce must return false for a duplicate nonce")
	}

	scoreAfter := rep.GetReputationScore(uhid)
	if scoreAfter >= scoreBefore {
		t.Errorf("replay attempt must decrease reputation: before=%.4f after=%.4f",
			scoreBefore, scoreAfter)
	}
}

// TestValidateAndRecordNonce_FirstTime_NoReputationFired verifies that a fresh
// nonce returns true and does not alter the sender's reputation score.
func TestValidateAndRecordNonce_FirstTime_NoReputationFired(t *testing.T) {
	svc := newTestService()
	defer svc.Close()

	rep := reputation.NewNodeReputationService()
	svc.SetReputation(rep)

	const uhid = "frank"
	n := nonce(0xAA, 0xBB, 0xCC, 0xDD, 0x01, 0x02, 0x03, 0x04)

	scoreBefore := rep.GetReputationScore(uhid)

	if !svc.ValidateAndRecordNonce(uhid, n) {
		t.Fatal("ValidateAndRecordNonce must return true for a fresh nonce")
	}

	scoreAfter := rep.GetReputationScore(uhid)
	if scoreAfter != scoreBefore {
		t.Errorf("reputation must not change for a fresh nonce: before=%.4f after=%.4f",
			scoreBefore, scoreAfter)
	}
}

// TestNotifySignatureFailure_FiresReputation verifies that NotifySignatureFailure
// applies the signature-failure penalty to the named UHID.
func TestNotifySignatureFailure_FiresReputation(t *testing.T) {
	svc := newTestService()
	defer svc.Close()

	rep := reputation.NewNodeReputationService()
	svc.SetReputation(rep)

	const uhid = "mallory"

	scoreBefore := rep.GetReputationScore(uhid)
	svc.NotifySignatureFailure(uhid)
	scoreAfter := rep.GetReputationScore(uhid)

	if scoreAfter >= scoreBefore {
		t.Errorf("signature failure must decrease reputation: before=%.4f after=%.4f",
			scoreBefore, scoreAfter)
	}
}

// TestNoReputation_NoError verifies that ValidateAndRecordNonce and
// NotifySignatureFailure do not panic when no reputation service is wired.
func TestNoReputation_NoError(t *testing.T) {
	svc := newTestService()
	defer svc.Close()
	// reputation is nil by default — no SetReputation call.

	n := nonce(0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9, 0xF8)
	const uhid = "ghost"

	// Fresh nonce with nil reputation must not panic.
	if !svc.ValidateAndRecordNonce(uhid, n) {
		t.Fatal("ValidateAndRecordNonce must return true for a fresh nonce")
	}

	// Duplicate nonce with nil reputation must return false without panic.
	if svc.ValidateAndRecordNonce(uhid, n) {
		t.Fatal("ValidateAndRecordNonce must return false for a duplicate nonce")
	}

	// NotifySignatureFailure with nil reputation must not panic.
	svc.NotifySignatureFailure(uhid)
}
