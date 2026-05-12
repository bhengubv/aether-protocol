// SPDX-License-Identifier: MIT

package reputation

import (
	"testing"
)

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

// testOpts returns detector options tuned for fast, deterministic unit tests.
func testOpts() AnomalyDetectorOptions {
	return AnomalyDetectorOptions{
		VolumeWindowMs:        100,
		VolumeSpikeMultiplier: 2.0,
		EwmaAlpha:             0.20,
		ScatterWindowMs:       60_000,
		ScatterThreshold:      3,
		GeohashPrefixLength:   4,
		GeohashRateLimitMs:    0, // every mismatch fires
	}
}

func newDetector() (*NodeReputationService, *BehavioralAnomalyDetector) {
	rep := NewNodeReputationService()
	det := NewBehavioralAnomalyDetector(rep, testOpts())
	return rep, det
}

// ---------------------------------------------------------------------------
// Volume spike tests
// ---------------------------------------------------------------------------

// TestVolume_NoSpikeFirstWindow: the first window only seeds the EWMA baseline;
// no flood signal should be fired.
func TestAnomalyVolume_NoSpikeFirstWindow(t *testing.T) {
	rep, det := newDetector()

	// Send 10 packets in window 0 [0, 100).
	var ts int64 = 0
	for i := 0; i < 10; i++ {
		det.ObservePacket("alice", "bob", ts)
		ts++
	}
	// Roll into window 1 — this is when the baseline gets set from window 0's count.
	det.ObservePacket("alice", "bob", 200)

	// Baseline is now 10.0.  Window 1 has count=1 which is way below 2×10=20.
	// No flood signal should have fired at all.
	got := rep.GetReputationScore("alice")
	if got != 1.0 {
		t.Fatalf("expected no flood penalty in first window, score=1.0, got %v", got)
	}
}

// TestVolume_SpikeFires: after a normal baseline window, a spike window should
// fire RecordRreqFloodAttempt.
func TestAnomalyVolume_SpikeFires(t *testing.T) {
	rep, det := newDetector()

	// Window 0 [0, 100): 5 packets → baseline = 5.
	for i := int64(0); i < 5; i++ {
		det.ObservePacket("alice", "bob", i)
	}

	// Window 1 [100, 200): 20 packets → count=20 > 2×5=10 → flood.
	for i := int64(100); i < 120; i++ {
		det.ObservePacket("alice", "bob", i)
	}

	// Rolling into window 2 triggers evaluation of window 1.
	det.ObservePacket("alice", "bob", 200)

	// Expect exactly one RecordRreqFloodAttempt → score = 1.0 - 0.05 = 0.95.
	got := rep.GetReputationScore("alice")
	want := 0.95
	if !approxEqual(got, want) {
		t.Fatalf("expected flood penalty applied, want %v got %v", want, got)
	}
}

// TestVolume_NoSpikeSameWindow: packets within the current window should not
// trigger evaluation; no penalty is applied until the window rolls.
func TestAnomalyVolume_NoSpikeSameWindow(t *testing.T) {
	rep, det := newDetector()

	// All 50 packets arrive within window 0 (ts < 100).
	for i := int64(0); i < 50; i++ {
		det.ObservePacket("alice", "bob", i)
	}

	// No window roll yet → no baseline → no flood.
	got := rep.GetReputationScore("alice")
	if got != 1.0 {
		t.Fatalf("expected no penalty within same window, got %v", got)
	}
}

// TestVolume_NoSpikeSmallEwma: after several normal windows the EWMA converges
// and a moderate count should not exceed the multiplier threshold.
func TestAnomalyVolume_NoSpikeSmallEwma(t *testing.T) {
	rep, det := newDetector()

	// Send 5 packets per window for 5 consecutive windows.
	// EWMA will converge toward 5.  The 6th window also sends 5 packets
	// (not a spike).
	for w := int64(0); w < 6; w++ {
		base := w * 200 // each window is 200 ms apart (window size = 100 ms)
		for i := int64(0); i < 5; i++ {
			det.ObservePacket("alice", "bob", base+i)
		}
	}
	// Roll to trigger evaluation of the 6th window.
	det.ObservePacket("alice", "bob", 6*200)

	got := rep.GetReputationScore("alice")
	if got != 1.0 {
		t.Fatalf("EWMA converged; moderate count should not spike, got %v", got)
	}
}

// ---------------------------------------------------------------------------
// Destination scatter tests
// ---------------------------------------------------------------------------

// TestScatter_BelowThreshold: fewer unique destinations than ScatterThreshold
// should produce no penalty.
func TestAnomalyScatter_BelowThreshold(t *testing.T) {
	rep, det := newDetector()

	// ScatterThreshold = 3; send to 3 unique destinations (not > 3).
	det.ObservePacket("alice", "d1", 0)
	det.ObservePacket("alice", "d2", 1)
	det.ObservePacket("alice", "d3", 2)

	got := rep.GetReputationScore("alice")
	if got != 1.0 {
		t.Fatalf("expected no scatter penalty at threshold, got %v", got)
	}
}

// TestScatter_AtThreshold: exceeding ScatterThreshold unique destinations fires
// exactly one flood signal per ObservePacket call that crosses it.
func TestAnomalyScatter_AtThreshold(t *testing.T) {
	rep, det := newDetector()

	// Send to 3 unique dests (= threshold, no fire).
	det.ObservePacket("alice", "d1", 0)
	det.ObservePacket("alice", "d2", 1)
	det.ObservePacket("alice", "d3", 2)

	// 4th unique dest → len(seen)=4 > 3 → flood fired.
	det.ObservePacket("alice", "d4", 3)

	// One RecordRreqFloodAttempt → 1.0 - 0.05 = 0.95.
	got := rep.GetReputationScore("alice")
	want := 0.95
	if !approxEqual(got, want) {
		t.Fatalf("expected scatter flood penalty, want %v got %v", want, got)
	}
}

// TestScatter_OldEntriesPruned: destinations older than ScatterWindowMs should
// be pruned and not count toward the scatter total.
func TestAnomalyScatter_OldEntriesPruned(t *testing.T) {
	rep, det := newDetector()

	// ScatterWindowMs = 60_000 ms = 60 s.
	// Send 3 packets far in the past.
	det.ObservePacket("alice", "old1", 0)
	det.ObservePacket("alice", "old2", 1)
	det.ObservePacket("alice", "old3", 2)

	// Now send 4 new packets well beyond the scatter window; old ones are pruned.
	// The cutoff for ts=70_000 is 70_000 - 60_000 = 10_000, so entries at 0/1/2 are pruned.
	det.ObservePacket("alice", "new1", 70_000)
	det.ObservePacket("alice", "new2", 70_001)
	det.ObservePacket("alice", "new3", 70_002)
	// 3 unique in-window dests — not > threshold, no flood.

	got := rep.GetReputationScore("alice")
	if got != 1.0 {
		t.Fatalf("expected old entries pruned; no penalty, got %v", got)
	}
}

// ---------------------------------------------------------------------------
// Geohash mismatch tests
// ---------------------------------------------------------------------------

// TestGeohash_MatchNoFire: matching geohash prefixes should not fire.
func TestAnomalyGeohash_MatchNoFire(t *testing.T) {
	rep, det := newDetector()

	det.checkGeohash("alice", "u33d9xyz", "u33d0abc", 1000)

	got := rep.GetReputationScore("alice")
	if got != 1.0 {
		t.Fatalf("matching prefix should not fire, got %v", got)
	}
}

// TestGeohash_MismatchFires: mismatched geohash prefixes should fire
// RecordSignatureFailure.
func TestAnomalyGeohash_MismatchFires(t *testing.T) {
	rep, det := newDetector()

	// GeohashRateLimitMs = 0 → always fires.
	det.checkGeohash("alice", "u33dXXX", "u34dXXX", 1000)

	// RecordSignatureFailure → score = 1.0 - 0.20 = 0.80.
	got := rep.GetReputationScore("alice")
	want := 0.80
	if !approxEqual(got, want) {
		t.Fatalf("expected sig-failure penalty, want %v got %v", want, got)
	}
}

// TestGeohash_RateLimit: with GeohashRateLimitMs > 0 a second mismatch within
// the limit window should NOT fire again.
func TestAnomalyGeohash_RateLimit(t *testing.T) {
	rep := NewNodeReputationService()
	opts := testOpts()
	opts.GeohashRateLimitMs = 5000 // 5 second rate limit
	det := NewBehavioralAnomalyDetector(rep, opts)

	// First mismatch at t=1000 → fires.
	det.checkGeohash("alice", "u33dXXX", "u34dXXX", 1000)
	// Second mismatch at t=2000 → within rate limit (2000-1000=1000 < 5000) → no fire.
	det.checkGeohash("alice", "u33dXXX", "u34dXXX", 2000)

	// Only one sig-failure penalty: 1.0 - 0.20 = 0.80.
	got := rep.GetReputationScore("alice")
	want := 0.80
	if !approxEqual(got, want) {
		t.Fatalf("rate limit should suppress second signal, want %v got %v", want, got)
	}

	// Third mismatch at t=7000 → gap from lastSignal (1000) = 6000 >= 5000 → fires.
	det.checkGeohash("alice", "u33dXXX", "u34dXXX", 7000)

	// Two sig-failure penalties: 1.0 - 0.40 = 0.60.
	got = rep.GetReputationScore("alice")
	want = 0.60
	if !approxEqual(got, want) {
		t.Fatalf("after rate limit expires, second signal should fire, want %v got %v", want, got)
	}
}

// ---------------------------------------------------------------------------
// SPK sig failure passthrough
// ---------------------------------------------------------------------------

// TestSpkSigFailure_Passthrough: ObserveSpkSigFailure must directly call
// RecordSignatureFailure.
func TestAnomalySpkSigFailure_Passthrough(t *testing.T) {
	rep, det := newDetector()

	det.ObserveSpkSigFailure("alice")

	got := rep.GetReputationScore("alice")
	want := 0.80
	if !approxEqual(got, want) {
		t.Fatalf("expected sig-failure penalty, want %v got %v", want, got)
	}
}
