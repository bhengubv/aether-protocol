// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"context"
	"sync/atomic"
	"testing"
	"time"
)

// syntheticClock is a controllable time source for rotation tests.
type syntheticClock struct {
	now atomic.Int64 // unix nanos
}

func newSyntheticClock(start time.Time) *syntheticClock {
	c := &syntheticClock{}
	c.now.Store(start.UnixNano())
	return c
}

func (c *syntheticClock) Now() time.Time {
	return time.Unix(0, c.now.Load())
}

func (c *syntheticClock) Advance(d time.Duration) {
	c.now.Add(int64(d))
}

// TestSpkRotation_NoRotationWithinInterval verifies that successive bundle
// calls within the rotation window reuse the same active SPK.
func TestSpkRotation_NoRotationWithinInterval(t *testing.T) {
	clock := newSyntheticClock(time.Now())
	rot := SignedPreKeyRotationOptions{
		RotationInterval:     7 * 24 * time.Hour,
		RetainedHistoryCount: 3,
	}

	svc, err := NewSignalProtocolService(WithRotationOptions(rot), WithNowProvider(clock.Now))
	if err != nil {
		t.Fatalf("NewSignalProtocolService: %v", err)
	}

	first, err := svc.GeneratePreKeyBundle("alice")
	if err != nil {
		t.Fatalf("first: %v", err)
	}
	clock.Advance(24 * time.Hour) // 1 day, well within 7-day interval
	second, err := svc.GeneratePreKeyBundle("alice")
	if err != nil {
		t.Fatalf("second: %v", err)
	}
	if first.SignedPreKeyID != second.SignedPreKeyID {
		t.Errorf("SPK rotated within window: first=%d second=%d", first.SignedPreKeyID, second.SignedPreKeyID)
	}
	if !bytes.Equal(first.SignedPreKey, second.SignedPreKey) {
		t.Errorf("SPK pub differs across same-window bundles")
	}
	if svc.SignedPreKeyHistoryCount() != 1 {
		t.Errorf("history count: got %d want 1", svc.SignedPreKeyHistoryCount())
	}
}

// TestSpkRotation_RotatesAfterInterval verifies that advancing past the
// rotation interval triggers a fresh SPK on the next bundle call.
func TestSpkRotation_RotatesAfterInterval(t *testing.T) {
	clock := newSyntheticClock(time.Now())
	rot := SignedPreKeyRotationOptions{
		RotationInterval:     7 * 24 * time.Hour,
		RetainedHistoryCount: 3,
	}
	svc, _ := NewSignalProtocolService(WithRotationOptions(rot), WithNowProvider(clock.Now))

	first, _ := svc.GeneratePreKeyBundle("alice")
	clock.Advance(8 * 24 * time.Hour) // beyond the 7-day window
	second, _ := svc.GeneratePreKeyBundle("alice")

	if first.SignedPreKeyID == second.SignedPreKeyID {
		t.Errorf("SPK NOT rotated past interval — id stayed at %d", first.SignedPreKeyID)
	}
	if bytes.Equal(first.SignedPreKey, second.SignedPreKey) {
		t.Errorf("SPK pub identical past interval — rotation broken")
	}
	if svc.SignedPreKeyHistoryCount() != 2 {
		t.Errorf("history count after one rotation: got %d want 2", svc.SignedPreKeyHistoryCount())
	}
}

// TestSpkRotation_RetainedSpkStillDecrypts is the most important rotation
// test: a peer that processed bundle B1 (under SPK1) and is mid-conversation
// when the responder rotates SPK to SPK2 must still get its first
// PreKey-typed message decrypted, because SPK1 is in the retained history.
func TestSpkRotation_RetainedSpkStillDecrypts(t *testing.T) {
	clock := newSyntheticClock(time.Now())
	rot := SignedPreKeyRotationOptions{
		RotationInterval:     7 * 24 * time.Hour,
		RetainedHistoryCount: 3,
	}
	bob, err := NewSignalProtocolService(WithRotationOptions(rot), WithNowProvider(clock.Now))
	if err != nil {
		t.Fatalf("bob: %v", err)
	}
	alice, _ := NewSignalProtocolService()

	// Bob generates bundle B1 (under SPK1). Alice processes it.
	b1, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	if err := alice.ProcessPreKeyBundle(b1); err != nil {
		t.Fatalf("Alice ProcessPreKeyBundle: %v", err)
	}

	// Time passes; Bob rotates SPK by generating a new bundle for someone else.
	// (Forces a rotation since we cross the interval.)
	clock.Advance(8 * 24 * time.Hour)
	if _, err := bob.GeneratePreKeyBundle("bob"); err != nil {
		t.Fatalf("Bob bundle after rotation: %v", err)
	}
	if bob.ActiveSignedPreKeyID() == b1.SignedPreKeyID {
		t.Fatalf("SPK did not actually rotate")
	}
	if bob.SignedPreKeyHistoryCount() < 2 {
		t.Fatalf("history count after rotation: got %d, want >= 2", bob.SignedPreKeyHistoryCount())
	}

	// Alice now sends her first message — the PreKey envelope still
	// references SPK1 (which Alice cached when she processed B1).
	first, err := alice.Encrypt("bob", []byte("retained-spk-test"))
	if err != nil {
		t.Fatalf("alice.Encrypt: %v", err)
	}
	if first.UsedSignedPreKeyID != b1.SignedPreKeyID {
		t.Fatalf("first.UsedSignedPreKeyID: got %d want %d (Alice should reference the SPK she captured)",
			first.UsedSignedPreKeyID, b1.SignedPreKeyID)
	}

	// Bob must decrypt — SPK1 is in the retained history.
	plain, err := bob.Decrypt("alice", first)
	if err != nil {
		t.Fatalf("bob.Decrypt with retained SPK: %v", err)
	}
	if !bytes.Equal(plain, []byte("retained-spk-test")) {
		t.Errorf("plain: got %q want retained-spk-test", plain)
	}
}

// TestSpkRotation_PrunedSpkRejected verifies that an SPK rotated outside
// the retention window can no longer establish a session.
func TestSpkRotation_PrunedSpkRejected(t *testing.T) {
	clock := newSyntheticClock(time.Now())
	rot := SignedPreKeyRotationOptions{
		RotationInterval:     7 * 24 * time.Hour,
		RetainedHistoryCount: 1, // small budget — rotates out fast
	}
	bob, _ := NewSignalProtocolService(WithRotationOptions(rot), WithNowProvider(clock.Now))
	alice, _ := NewSignalProtocolService()

	// Bob's initial SPK (SPK1).
	b1, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	if err := alice.ProcessPreKeyBundle(b1); err != nil {
		t.Fatalf("ProcessPreKeyBundle: %v", err)
	}

	// Force two rotations so SPK1 falls outside the retention window
	// (RetainedHistoryCount=1 means we keep active + 1 prior; SPK1 must
	// be evicted after a second rotation).
	clock.Advance(8 * 24 * time.Hour)
	if _, err := bob.GeneratePreKeyBundle("bob"); err != nil {
		t.Fatalf("Bob first rotation: %v", err)
	}
	clock.Advance(8 * 24 * time.Hour)
	if _, err := bob.GeneratePreKeyBundle("bob"); err != nil {
		t.Fatalf("Bob second rotation: %v", err)
	}
	if bob.SignedPreKeyHistoryCount() != 2 {
		t.Fatalf("history count after two rotations w/ retain=1: got %d want 2",
			bob.SignedPreKeyHistoryCount())
	}

	// Alice tries to establish — first PreKey message references the
	// pruned SPK1 — Bob must reject.
	first, err := alice.Encrypt("bob", []byte("should-fail"))
	if err != nil {
		t.Fatalf("alice.Encrypt: %v", err)
	}
	if first.UsedSignedPreKeyID != b1.SignedPreKeyID {
		t.Fatalf("Alice did not reference SPK1; test invalid")
	}
	if _, err := bob.Decrypt("alice", first); err == nil {
		t.Errorf("bob.Decrypt with pruned SPK: expected error, got nil")
	}
}

// TestSpkRotation_ManualRotateMethod verifies the public RotateSignedPreKey API.
func TestSpkRotation_ManualRotateMethod(t *testing.T) {
	clock := newSyntheticClock(time.Now())
	rot := SignedPreKeyRotationOptions{
		RotationInterval:     7 * 24 * time.Hour,
		RetainedHistoryCount: 2,
	}
	svc, _ := NewSignalProtocolService(WithRotationOptions(rot), WithNowProvider(clock.Now))

	// First call: no SPK yet -> rotate (i.e. generate the first one) returns true.
	rotated, err := svc.RotateSignedPreKey(context.Background())
	if err != nil {
		t.Fatalf("RotateSignedPreKey first: %v", err)
	}
	if !rotated {
		t.Errorf("first RotateSignedPreKey: got false, want true")
	}
	if svc.SignedPreKeyHistoryCount() != 1 {
		t.Errorf("history count after first rotate: got %d want 1", svc.SignedPreKeyHistoryCount())
	}

	// Second call: within the rotation window -> no rotation needed.
	clock.Advance(1 * time.Hour)
	rotated, err = svc.RotateSignedPreKey(context.Background())
	if err != nil {
		t.Fatalf("RotateSignedPreKey second: %v", err)
	}
	if rotated {
		t.Errorf("RotateSignedPreKey within window: got true, want false")
	}

	// Third call: past the window -> rotation triggered.
	clock.Advance(8 * 24 * time.Hour)
	rotated, err = svc.RotateSignedPreKey(context.Background())
	if err != nil {
		t.Fatalf("RotateSignedPreKey third: %v", err)
	}
	if !rotated {
		t.Errorf("RotateSignedPreKey past window: got false, want true")
	}
	if svc.SignedPreKeyHistoryCount() != 2 {
		t.Errorf("history count after past-window rotate: got %d want 2", svc.SignedPreKeyHistoryCount())
	}
}

// TestSpkRotation_HistoryPersistsAcrossRestart verifies that the SPK
// history (active + retained) survives a restart.
func TestSpkRotation_HistoryPersistsAcrossRestart(t *testing.T) {
	clock := newSyntheticClock(time.Now())
	rot := SignedPreKeyRotationOptions{
		RotationInterval:     7 * 24 * time.Hour,
		RetainedHistoryCount: 3,
	}

	// Use an in-memory store so the SPK history can persist.
	preKeyStore := NewInMemoryPreKeyStore()

	bob1, err := NewSignalProtocolService(
		WithRotationOptions(rot),
		WithNowProvider(clock.Now),
		WithPreKeyStore(preKeyStore),
	)
	if err != nil {
		t.Fatalf("bob1: %v", err)
	}

	// Trigger 3 SPKs (initial + 2 rotations).
	bob1.GeneratePreKeyBundle("bob")
	clock.Advance(8 * 24 * time.Hour)
	bob1.GeneratePreKeyBundle("bob")
	clock.Advance(8 * 24 * time.Hour)
	bob1.GeneratePreKeyBundle("bob")
	if got := bob1.SignedPreKeyHistoryCount(); got != 3 {
		t.Fatalf("bob1 history: got %d want 3", got)
	}
	bob1ActiveID := bob1.ActiveSignedPreKeyID()

	// Restart — clock is at the same time, no further rotation should occur.
	bob2, err := NewSignalProtocolService(
		WithRotationOptions(rot),
		WithNowProvider(clock.Now),
		WithPreKeyStore(preKeyStore),
	)
	if err != nil {
		t.Fatalf("bob2: %v", err)
	}
	if got := bob2.SignedPreKeyHistoryCount(); got != 3 {
		t.Errorf("bob2 history after restart: got %d want 3", got)
	}
	if bob2.ActiveSignedPreKeyID() != bob1ActiveID {
		t.Errorf("bob2 active SPK id: got %d want %d", bob2.ActiveSignedPreKeyID(), bob1ActiveID)
	}
}
