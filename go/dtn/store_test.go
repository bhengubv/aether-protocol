// SPDX-License-Identifier: MIT

package dtn

import (
	"context"
	"testing"
	"time"

	"github.com/bhengubv/aether-protocol/go/models"
)

// ── helpers ───────────────────────────────────────────────────────────────────

func freshBundle(id, sender, recipient string) *models.DtnBundle {
	return &models.DtnBundle{
		ID:               id,
		SenderUhid:       sender,
		RecipientUhid:    recipient,
		EncryptedPayload: []byte{1, 2, 3},
		Status:           models.DtnStatusPending,
		CopyCount:        1,
		MaxCopies:        3,
		ExpiresAt:        time.Now().Add(time.Hour),
	}
}

func expiredBundle(id string) *models.DtnBundle {
	b := freshBundle(id, "alice", "bob")
	b.ExpiresAt = time.Now().Add(-time.Second) // already expired
	return b
}

var ctx = context.Background()

// ── InMemoryBundleStore ───────────────────────────────────────────────────────

// ── Get ───────────────────────────────────────────────────────────────────────

func TestBundleStore_GetReturnsNilForUnknownID(t *testing.T) {
	s := NewInMemoryBundleStore()
	got, err := s.Get(ctx, "unknown-id")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != nil {
		t.Errorf("expected nil for unknown ID, got %+v", got)
	}
}

func TestBundleStore_SaveAndGetRoundTrip(t *testing.T) {
	s := NewInMemoryBundleStore()
	b := freshBundle("b1", "alice", "bob")
	if err := s.Save(ctx, b); err != nil {
		t.Fatalf("Save: %v", err)
	}
	got, err := s.Get(ctx, "b1")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if got == nil {
		t.Fatal("expected non-nil, got nil")
	}
	if got.SenderUhid != "alice" || got.RecipientUhid != "bob" {
		t.Errorf("unexpected bundle: %+v", got)
	}
}

func TestBundleStore_SaveOverwritesExistingEntry(t *testing.T) {
	s := NewInMemoryBundleStore()
	b := freshBundle("b1", "alice", "bob")
	_ = s.Save(ctx, b)
	updated := *b
	updated.Status = models.DtnStatusInCustody
	_ = s.Save(ctx, &updated)
	got, _ := s.Get(ctx, "b1")
	if got == nil || got.Status != models.DtnStatusInCustody {
		t.Errorf("expected InCustody, got %+v", got)
	}
}

// ── Remove ────────────────────────────────────────────────────────────────────

func TestBundleStore_RemoveDeletesBundle(t *testing.T) {
	s := NewInMemoryBundleStore()
	_ = s.Save(ctx, freshBundle("b1", "a", "b"))
	if err := s.Remove(ctx, "b1"); err != nil {
		t.Fatalf("Remove: %v", err)
	}
	got, _ := s.Get(ctx, "b1")
	if got != nil {
		t.Errorf("expected nil after Remove, got %+v", got)
	}
}

func TestBundleStore_RemoveNonexistentIsNoOp(t *testing.T) {
	s := NewInMemoryBundleStore()
	if err := s.Remove(ctx, "ghost"); err != nil {
		t.Errorf("unexpected error removing nonexistent bundle: %v", err)
	}
}

// ── GetActive ─────────────────────────────────────────────────────────────────

func TestBundleStore_GetActiveReturnsEmptyInitially(t *testing.T) {
	s := NewInMemoryBundleStore()
	active, err := s.GetActive(ctx)
	if err != nil {
		t.Fatalf("GetActive: %v", err)
	}
	if len(active) != 0 {
		t.Errorf("expected empty, got %d entries", len(active))
	}
}

func TestBundleStore_GetActiveReturnsPendingNonExpired(t *testing.T) {
	s := NewInMemoryBundleStore()
	_ = s.Save(ctx, freshBundle("b1", "a", "b"))
	active, _ := s.GetActive(ctx)
	if len(active) != 1 {
		t.Fatalf("expected 1 active bundle, got %d", len(active))
	}
	if active[0].ID != "b1" {
		t.Errorf("unexpected ID: %s", active[0].ID)
	}
}

func TestBundleStore_GetActiveReturnsInCustodyNonExpired(t *testing.T) {
	s := NewInMemoryBundleStore()
	b := freshBundle("b2", "a", "b")
	b.Status = models.DtnStatusInCustody
	_ = s.Save(ctx, b)
	active, _ := s.GetActive(ctx)
	if len(active) != 1 {
		t.Errorf("expected 1 InCustody bundle, got %d", len(active))
	}
}

func TestBundleStore_GetActiveExcludesExpiredBundles(t *testing.T) {
	s := NewInMemoryBundleStore()
	_ = s.Save(ctx, expiredBundle("exp"))
	active, _ := s.GetActive(ctx)
	if len(active) != 0 {
		t.Errorf("expected empty (expired bundle excluded), got %d", len(active))
	}
}

func TestBundleStore_GetActiveExcludesDeliveredBundles(t *testing.T) {
	s := NewInMemoryBundleStore()
	b := freshBundle("b3", "a", "b")
	b.Status = models.DtnStatusDelivered
	_ = s.Save(ctx, b)
	active, _ := s.GetActive(ctx)
	if len(active) != 0 {
		t.Errorf("expected empty (Delivered excluded), got %d", len(active))
	}
}

// ── GetActiveCount ────────────────────────────────────────────────────────────

func TestBundleStore_GetActiveCountReturnsCorrectCount(t *testing.T) {
	s := NewInMemoryBundleStore()
	_ = s.Save(ctx, freshBundle("b1", "a", "b"))
	_ = s.Save(ctx, freshBundle("b2", "a", "b"))
	delivered := freshBundle("b3", "a", "b")
	delivered.Status = models.DtnStatusDelivered
	_ = s.Save(ctx, delivered)
	count, err := s.GetActiveCount(ctx)
	if err != nil {
		t.Fatalf("GetActiveCount: %v", err)
	}
	if count != 2 {
		t.Errorf("expected 2 active, got %d", count)
	}
}

// ── SaveCustody / GetCustodyRecords ───────────────────────────────────────────

func TestBundleStore_SaveAndGetCustodyRoundTrip(t *testing.T) {
	s := NewInMemoryBundleStore()
	rec := &models.CustodyRecord{
		ID:       "c1",
		BundleID: "b1",
		FromUhid: "alice",
		ToUhid:   "bob",
		Accepted: true,
	}
	if err := s.SaveCustody(ctx, rec); err != nil {
		t.Fatalf("SaveCustody: %v", err)
	}
	records, err := s.GetCustodyRecords(ctx, "b1")
	if err != nil {
		t.Fatalf("GetCustodyRecords: %v", err)
	}
	if len(records) != 1 {
		t.Fatalf("expected 1 record, got %d", len(records))
	}
	if records[0].FromUhid != "alice" || records[0].ToUhid != "bob" || !records[0].Accepted {
		t.Errorf("unexpected custody record: %+v", records[0])
	}
}

func TestBundleStore_GetCustodyRecordsReturnsEmptyForUnknownBundle(t *testing.T) {
	s := NewInMemoryBundleStore()
	records, _ := s.GetCustodyRecords(ctx, "no-such-bundle")
	if len(records) != 0 {
		t.Errorf("expected empty, got %d records", len(records))
	}
}

func TestBundleStore_GetCustodyRecordsFiltersByBundleID(t *testing.T) {
	s := NewInMemoryBundleStore()
	_ = s.SaveCustody(ctx, &models.CustodyRecord{ID: "c1", BundleID: "b-A", FromUhid: "a", ToUhid: "b", Accepted: true})
	_ = s.SaveCustody(ctx, &models.CustodyRecord{ID: "c2", BundleID: "b-B", FromUhid: "c", ToUhid: "d", Accepted: false})
	records, _ := s.GetCustodyRecords(ctx, "b-A")
	if len(records) != 1 {
		t.Fatalf("expected 1 record for b-A, got %d", len(records))
	}
	if records[0].BundleID != "b-A" {
		t.Errorf("unexpected bundleID: %s", records[0].BundleID)
	}
}

// ── ExpireStale ───────────────────────────────────────────────────────────────

func TestBundleStore_ExpireStaleMarksExpiredBundles(t *testing.T) {
	s := NewInMemoryBundleStore()
	b := expiredBundle("stale")
	_ = s.Save(ctx, b)
	count, err := s.ExpireStale(ctx)
	if err != nil {
		t.Fatalf("ExpireStale: %v", err)
	}
	if count != 1 {
		t.Errorf("expected 1 expired, got %d", count)
	}
	got, _ := s.Get(ctx, "stale")
	if got == nil || got.Status != models.DtnStatusExpired {
		t.Errorf("expected Expired status, got %+v", got)
	}
}

func TestBundleStore_ExpireStaleReturnsZeroWhenNothingExpired(t *testing.T) {
	s := NewInMemoryBundleStore()
	_ = s.Save(ctx, freshBundle("fresh", "a", "b"))
	count, _ := s.ExpireStale(ctx)
	if count != 0 {
		t.Errorf("expected 0, got %d", count)
	}
}

func TestBundleStore_ExpireStaleDoesNotDoubleMarkAlreadyExpired(t *testing.T) {
	s := NewInMemoryBundleStore()
	b := expiredBundle("already")
	b.Status = models.DtnStatusExpired
	_ = s.Save(ctx, b)
	count, _ := s.ExpireStale(ctx)
	if count != 0 {
		t.Errorf("already-Expired bundle should not be counted again, got count=%d", count)
	}
}

func TestBundleStore_ExpireStaleLeaveFreshBundlesUnchanged(t *testing.T) {
	s := NewInMemoryBundleStore()
	fresh := freshBundle("fresh", "alice", "bob")
	_ = s.Save(ctx, fresh)
	_ = s.Save(ctx, expiredBundle("stale"))
	_, _ = s.ExpireStale(ctx)
	got, _ := s.Get(ctx, "fresh")
	if got == nil || got.Status != models.DtnStatusPending {
		t.Errorf("fresh bundle should remain Pending, got %+v", got)
	}
}
