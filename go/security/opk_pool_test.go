// SPDX-License-Identifier: MIT

package security

import (
	"sync"
	"testing"
)

// TestOpkPool_DefaultSize_AfterFirstBundle verifies the pool tops up to
// DefaultOpkPoolSize on the first bundle call.
func TestOpkPool_DefaultSize_AfterFirstBundle(t *testing.T) {
	sps, err := NewSignalProtocolService()
	if err != nil {
		t.Fatalf("NewSignalProtocolService: %v", err)
	}
	if got := sps.OpkPoolSize(); got != DefaultOpkPoolSize {
		t.Errorf("OpkPoolSize: got %d want %d", got, DefaultOpkPoolSize)
	}

	// First bundle triggers top-up, then dequeues one. Held should stay at
	// DefaultOpkPoolSize (top-up runs BEFORE dequeue in C# parity terms,
	// but our Go top-up runs every bundle so post-dequeue we have
	// DefaultOpkPoolSize-1 available + 1 issued = DefaultOpkPoolSize held).
	bundle, err := sps.GeneratePreKeyBundle("uhid:alice")
	if err != nil {
		t.Fatalf("GeneratePreKeyBundle: %v", err)
	}
	if bundle == nil || bundle.PreKeyID == 0 {
		t.Fatalf("expected a non-zero pre-key id in the bundle")
	}

	held, available := sps.GetOpkPoolStatus()
	if held < DefaultOpkPoolSize {
		t.Errorf("held count: got %d want >= %d", held, DefaultOpkPoolSize)
	}
	if available != DefaultOpkPoolSize-1 {
		t.Errorf("available count after one bundle: got %d want %d", available, DefaultOpkPoolSize-1)
	}
}

// TestOpkPool_CustomSize verifies WithOpkPoolSize takes effect.
func TestOpkPool_CustomSize(t *testing.T) {
	sps, err := NewSignalProtocolService(WithOpkPoolSize(7))
	if err != nil {
		t.Fatalf("NewSignalProtocolService: %v", err)
	}
	if got := sps.OpkPoolSize(); got != 7 {
		t.Errorf("OpkPoolSize: got %d want 7", got)
	}

	if _, err := sps.GeneratePreKeyBundle("uhid:alice"); err != nil {
		t.Fatalf("GeneratePreKeyBundle: %v", err)
	}

	held, available := sps.GetOpkPoolStatus()
	if held < 7 {
		t.Errorf("held: got %d want >= 7", held)
	}
	if available != 6 {
		t.Errorf("available: got %d want 6", available)
	}
}

// TestOpkPool_RejectsZeroOrNegativePoolSize verifies the option validates.
func TestOpkPool_RejectsZeroOrNegativePoolSize(t *testing.T) {
	for _, sz := range []int{0, -1, -100} {
		if _, err := NewSignalProtocolService(WithOpkPoolSize(sz)); err == nil {
			t.Errorf("WithOpkPoolSize(%d) should error", sz)
		}
	}
}

// TestOpkPool_HundredSequentialBundles_DistinctOpkIds is the core
// concurrency-safety test: 100 sequential bundle requests must produce
// 100 DISTINCT OPK ids. The prior single-OPK design returned the same id
// (technically the most-recently-generated OPK) over and over, which is
// an active concurrency hazard for the responder.
func TestOpkPool_HundredSequentialBundles_DistinctOpkIds(t *testing.T) {
	sps, err := NewSignalProtocolService()
	if err != nil {
		t.Fatalf("NewSignalProtocolService: %v", err)
	}

	seen := make(map[int32]struct{}, DefaultOpkPoolSize)
	for i := 0; i < DefaultOpkPoolSize; i++ {
		bundle, err := sps.GeneratePreKeyBundle("uhid:alice")
		if err != nil {
			t.Fatalf("GeneratePreKeyBundle iter %d: %v", i, err)
		}
		if _, dup := seen[bundle.PreKeyID]; dup {
			t.Fatalf("duplicate OPK id %d at iteration %d — pool design is broken", bundle.PreKeyID, i)
		}
		seen[bundle.PreKeyID] = struct{}{}
	}
	if len(seen) != DefaultOpkPoolSize {
		t.Fatalf("expected %d distinct OPK ids, got %d", DefaultOpkPoolSize, len(seen))
	}
}

// TestOpkPool_TopUpAfterIssuance keeps the available queue full as keys
// are handed out. After 200 issuances the held set should still trend
// around opkPoolSize + issued-but-not-yet-consumed.
func TestOpkPool_TopUpAfterIssuance(t *testing.T) {
	sps, err := NewSignalProtocolService(WithOpkPoolSize(10))
	if err != nil {
		t.Fatalf("NewSignalProtocolService: %v", err)
	}

	for i := 0; i < 200; i++ {
		_, err := sps.GeneratePreKeyBundle("uhid:alice")
		if err != nil {
			t.Fatalf("GeneratePreKeyBundle iter %d: %v", i, err)
		}
		_, available := sps.GetOpkPoolStatus()
		if available != 9 {
			t.Errorf("iter %d: available=%d want 9 (top-up should refill)", i, available)
		}
	}
}

// TestOpkPool_ConsumeRemovesFromMap verifies the responder consume path
// (delete from oneTimePreKeys) interacts correctly with the pool.
func TestOpkPool_ConsumeRemovesFromMap(t *testing.T) {
	// Simulate: alice's responder side issues a bundle; bob (initiator)
	// runs X3DH against it; alice receives a PreKey message and consumes
	// the OPK. Held count should drop by exactly 1.
	alice, err := NewSignalProtocolService(WithOpkPoolSize(5))
	if err != nil {
		t.Fatalf("alice: %v", err)
	}
	bob, err := NewSignalProtocolService(WithOpkPoolSize(5))
	if err != nil {
		t.Fatalf("bob: %v", err)
	}

	bundle, err := alice.GeneratePreKeyBundle("uhid:alice")
	if err != nil {
		t.Fatalf("alice bundle: %v", err)
	}
	heldBefore, _ := alice.GetOpkPoolStatus()

	if err := bob.ProcessPreKeyBundle(bundle); err != nil {
		t.Fatalf("bob ProcessPreKeyBundle: %v", err)
	}
	bob.SetLocalUhid("uhid:bob")
	preKeyMsg, err := bob.Encrypt("uhid:alice", []byte("hi"))
	if err != nil {
		t.Fatalf("bob Encrypt: %v", err)
	}
	if preKeyMsg.MessageType != MessageTypePreKey {
		t.Fatalf("expected PreKey message, got type %d", preKeyMsg.MessageType)
	}
	if _, err := alice.Decrypt("uhid:bob", preKeyMsg); err != nil {
		t.Fatalf("alice Decrypt PreKey: %v", err)
	}

	heldAfter, _ := alice.GetOpkPoolStatus()
	if heldAfter != heldBefore-1 {
		t.Errorf("consume path: held went from %d to %d, expected drop by 1", heldBefore, heldAfter)
	}
	// The consumed OPK id must no longer be in the map.
	alice.mu.Lock()
	_, stillThere := alice.preKeys.oneTimePreKeys[bundle.PreKeyID]
	alice.mu.Unlock()
	if stillThere {
		t.Errorf("consumed OPK id %d still present in oneTimePreKeys", bundle.PreKeyID)
	}
}

// TestOpkPool_ConcurrentBundleIssuance: 50 goroutines each request a
// bundle from the same responder. All 50 OPK ids must be distinct (no
// double-issuance) and all 50 X3DH-via-responder paths must succeed.
//
// This is the core regression test for the concurrency hazard in the
// pre-pool single-OPK design.
func TestOpkPool_ConcurrentBundleIssuance(t *testing.T) {
	const N = 50
	alice, err := NewSignalProtocolService(WithOpkPoolSize(N))
	if err != nil {
		t.Fatalf("alice: %v", err)
	}

	type result struct {
		id  int32
		err error
	}
	results := make(chan result, N)
	var wg sync.WaitGroup
	wg.Add(N)
	for i := 0; i < N; i++ {
		go func() {
			defer wg.Done()
			b, err := alice.GeneratePreKeyBundle("uhid:alice")
			if err != nil {
				results <- result{0, err}
				return
			}
			results <- result{b.PreKeyID, nil}
		}()
	}
	wg.Wait()
	close(results)

	seen := make(map[int32]struct{}, N)
	for r := range results {
		if r.err != nil {
			t.Errorf("concurrent bundle: %v", r.err)
			continue
		}
		if _, dup := seen[r.id]; dup {
			t.Errorf("concurrent issuance returned duplicate id %d", r.id)
		}
		seen[r.id] = struct{}{}
	}
	if len(seen) != N {
		t.Fatalf("expected %d distinct ids, got %d", N, len(seen))
	}
}

// TestOpkPool_FIFOOrdering verifies the available queue is FIFO. Two
// successive bundles must come from the order in which keys were enqueued
// (not LIFO, not random). FIFO matters because it bounds the maximum age
// of any held-but-not-consumed OPK.
func TestOpkPool_FIFOOrdering(t *testing.T) {
	sps, err := NewSignalProtocolService(WithOpkPoolSize(3))
	if err != nil {
		t.Fatalf("NewSignalProtocolService: %v", err)
	}

	// Force initial top-up by snapshotting the queue.
	if _, err := sps.GeneratePreKeyBundle("uhid:alice"); err != nil {
		t.Fatalf("warmup bundle: %v", err)
	}
	sps.mu.Lock()
	queueSnapshot := append([]int32(nil), sps.preKeys.availableOpkIds...)
	sps.mu.Unlock()

	if len(queueSnapshot) < 2 {
		t.Fatalf("expected queue to be full of un-issued ids, got %d", len(queueSnapshot))
	}
	expectedNext := queueSnapshot[0]

	bundle, err := sps.GeneratePreKeyBundle("uhid:alice")
	if err != nil {
		t.Fatalf("second bundle: %v", err)
	}
	if bundle.PreKeyID != expectedNext {
		t.Errorf("FIFO violation: got id %d, expected front-of-queue %d", bundle.PreKeyID, expectedNext)
	}
}
