// SPDX-License-Identifier: MIT

package reputation

import (
	"math"
	"testing"
)

// approxEqual returns true when |got - want| < 1e-9.
func approxEqual(got, want float64) bool {
	return math.Abs(got-want) < 1e-9
}

func TestUnknownPeer_ReturnsOne(t *testing.T) {
	svc := NewNodeReputationService()
	got := svc.GetReputationScore("nobody")
	if got != 1.0 {
		t.Fatalf("expected 1.0 for unknown peer, got %v", got)
	}
}

func TestRreqFlood_ReducesScore(t *testing.T) {
	svc := NewNodeReputationService()
	svc.RecordRreqFloodAttempt("alice")
	got := svc.GetReputationScore("alice")
	want := 0.95
	if !approxEqual(got, want) {
		t.Fatalf("expected %v, got %v", want, got)
	}
}

func TestReplayAttempt_ReducesScore(t *testing.T) {
	svc := NewNodeReputationService()
	svc.RecordReplayAttempt("alice")
	got := svc.GetReputationScore("alice")
	want := 0.85
	if !approxEqual(got, want) {
		t.Fatalf("expected %v, got %v", want, got)
	}
}

func TestSignatureFailure_ReducesScore(t *testing.T) {
	svc := NewNodeReputationService()
	svc.RecordSignatureFailure("alice")
	got := svc.GetReputationScore("alice")
	want := 0.80
	if !approxEqual(got, want) {
		t.Fatalf("expected %v, got %v", want, got)
	}
}

func TestCustodyRefusal_ReducesScore(t *testing.T) {
	svc := NewNodeReputationService()
	svc.RecordCustodyRefusal("alice")
	got := svc.GetReputationScore("alice")
	want := 0.95
	if !approxEqual(got, want) {
		t.Fatalf("expected %v, got %v", want, got)
	}
}

func TestDeliveryFailure_ReducesScore(t *testing.T) {
	svc := NewNodeReputationService()
	svc.RecordDeliveryFailure("alice")
	got := svc.GetReputationScore("alice")
	want := 0.98
	if !approxEqual(got, want) {
		t.Fatalf("expected %v, got %v", want, got)
	}
}

func TestClampToZero(t *testing.T) {
	// 5 × signature failure: 1.0 - 5×0.20 = 0.0
	svc := NewNodeReputationService()
	for i := 0; i < 5; i++ {
		svc.RecordSignatureFailure("bad")
	}
	got := svc.GetReputationScore("bad")
	if got != 0.0 {
		t.Fatalf("expected 0.0 after 5 signature failures, got %v", got)
	}
}

func TestClampToOne(t *testing.T) {
	// 10 × delivery success starting from 1.0 must not exceed 1.0
	svc := NewNodeReputationService()
	for i := 0; i < 10; i++ {
		svc.RecordDeliverySuccess("good", 50)
	}
	got := svc.GetReputationScore("good")
	if got != 1.0 {
		t.Fatalf("expected 1.0 after many successes, got %v", got)
	}
}

func TestNoCrossContamination(t *testing.T) {
	svc := NewNodeReputationService()
	svc.RecordSignatureFailure("alice")

	aliceScore := svc.GetReputationScore("alice")
	bobScore := svc.GetReputationScore("bob")

	if !approxEqual(aliceScore, 0.80) {
		t.Fatalf("alice: expected 0.80, got %v", aliceScore)
	}
	if bobScore != 1.0 {
		t.Fatalf("bob should be unaffected, got %v", bobScore)
	}
}

func TestGetAllScores(t *testing.T) {
	svc := NewNodeReputationService()
	svc.RecordRreqFloodAttempt("alice")
	svc.RecordSignatureFailure("bob")

	all := svc.GetAllScores()
	if len(all) != 2 {
		t.Fatalf("expected 2 entries, got %d", len(all))
	}
	if !approxEqual(all["alice"], 0.95) {
		t.Fatalf("alice: expected 0.95, got %v", all["alice"])
	}
	if !approxEqual(all["bob"], 0.80) {
		t.Fatalf("bob: expected 0.80, got %v", all["bob"])
	}

	// Mutation of snapshot must not affect internal state.
	all["alice"] = 0.0
	if !approxEqual(svc.GetReputationScore("alice"), 0.95) {
		t.Fatal("GetAllScores returned a live map rather than a snapshot copy")
	}
}

func TestCompoundSignals(t *testing.T) {
	// RREQ −0.05, replay −0.15, sig −0.20 → 1.0 - 0.40 = 0.60
	svc := NewNodeReputationService()
	svc.RecordRreqFloodAttempt("attacker")
	svc.RecordReplayAttempt("attacker")
	svc.RecordSignatureFailure("attacker")
	got := svc.GetReputationScore("attacker")
	want := 0.60
	if !approxEqual(got, want) {
		t.Fatalf("expected %v, got %v", want, got)
	}
}
