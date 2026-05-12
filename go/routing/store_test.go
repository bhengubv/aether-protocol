// SPDX-License-Identifier: MIT

package routing

import (
	"context"
	"testing"
	"time"

	"github.com/bhengubv/aether-protocol/go/models"
)

// ── helpers ───────────────────────────────────────────────────────────────────

var rctx = context.Background()

func freshRoute(dest, nextHop string) *models.RouteEntry {
	return &models.RouteEntry{
		DestinationUhid: dest,
		NextHop:         nextHop,
		HopCount:        1,
		QualityScore:    50,
		ExpiresAt:       time.Now().Add(5 * time.Minute),
	}
}

func expiredRoute(dest string) *models.RouteEntry {
	return &models.RouteEntry{
		DestinationUhid: dest,
		NextHop:         dest,
		HopCount:        1,
		ExpiresAt:       time.Now().Add(-time.Second), // already expired
	}
}

// ── Get ───────────────────────────────────────────────────────────────────────

func TestRouteStore_GetReturnsNilForUnknownDestination(t *testing.T) {
	s := NewInMemoryRouteStore()
	got, err := s.Get(rctx, "unknown-uhid")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if got != nil {
		t.Errorf("expected nil for unknown destination, got %+v", got)
	}
}

func TestRouteStore_SaveAndGetRoundTrip(t *testing.T) {
	s := NewInMemoryRouteStore()
	r := freshRoute("node-b", "node-b")
	if err := s.Save(rctx, r); err != nil {
		t.Fatalf("Save: %v", err)
	}
	got, err := s.Get(rctx, "node-b")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if got == nil {
		t.Fatal("expected non-nil, got nil")
	}
	if got.DestinationUhid != "node-b" || got.NextHop != "node-b" || got.HopCount != 1 {
		t.Errorf("unexpected route: %+v", got)
	}
}

func TestRouteStore_SaveOverwritesExistingEntry(t *testing.T) {
	s := NewInMemoryRouteStore()
	_ = s.Save(rctx, freshRoute("node-c", "relay-1"))
	_ = s.Save(rctx, &models.RouteEntry{
		DestinationUhid: "node-c",
		NextHop:         "relay-2",
		HopCount:        2,
		ExpiresAt:       time.Now().Add(time.Hour),
	})
	got, _ := s.Get(rctx, "node-c")
	if got == nil || got.NextHop != "relay-2" || got.HopCount != 2 {
		t.Errorf("expected overwritten route with relay-2, got %+v", got)
	}
}

// ── Remove ────────────────────────────────────────────────────────────────────

func TestRouteStore_RemoveDeletesRoute(t *testing.T) {
	s := NewInMemoryRouteStore()
	_ = s.Save(rctx, freshRoute("node-d", "node-d"))
	if err := s.Remove(rctx, "node-d"); err != nil {
		t.Fatalf("Remove: %v", err)
	}
	got, _ := s.Get(rctx, "node-d")
	if got != nil {
		t.Errorf("expected nil after Remove, got %+v", got)
	}
}

func TestRouteStore_RemoveNonexistentDestinationIsNoOp(t *testing.T) {
	s := NewInMemoryRouteStore()
	if err := s.Remove(rctx, "ghost"); err != nil {
		t.Errorf("unexpected error removing nonexistent destination: %v", err)
	}
}

// ── GetAll ────────────────────────────────────────────────────────────────────

func TestRouteStore_GetAllReturnsEmptyInitially(t *testing.T) {
	s := NewInMemoryRouteStore()
	all, err := s.GetAll(rctx)
	if err != nil {
		t.Fatalf("GetAll: %v", err)
	}
	if len(all) != 0 {
		t.Errorf("expected empty, got %d entries", len(all))
	}
}

func TestRouteStore_GetAllReturnsAllSavedRoutes(t *testing.T) {
	s := NewInMemoryRouteStore()
	_ = s.Save(rctx, freshRoute("n1", "n1"))
	_ = s.Save(rctx, freshRoute("n2", "n2"))
	_ = s.Save(rctx, freshRoute("n3", "n3"))
	all, _ := s.GetAll(rctx)
	if len(all) != 3 {
		t.Errorf("expected 3 routes, got %d", len(all))
	}
}

func TestRouteStore_GetAllExcludesRemovedRoutes(t *testing.T) {
	s := NewInMemoryRouteStore()
	_ = s.Save(rctx, freshRoute("keep",   "keep"))
	_ = s.Save(rctx, freshRoute("remove", "remove"))
	_ = s.Remove(rctx, "remove")
	all, _ := s.GetAll(rctx)
	if len(all) != 1 {
		t.Fatalf("expected 1 route, got %d", len(all))
	}
	if all[0].DestinationUhid != "keep" {
		t.Errorf("unexpected destination: %s", all[0].DestinationUhid)
	}
}

// ── PruneExpired ──────────────────────────────────────────────────────────────

func TestRouteStore_PruneExpiredReturnsZeroWhenNothingExpired(t *testing.T) {
	s := NewInMemoryRouteStore()
	_ = s.Save(rctx, freshRoute("node-e", "node-e"))
	count, err := s.PruneExpired(rctx)
	if err != nil {
		t.Fatalf("PruneExpired: %v", err)
	}
	if count != 0 {
		t.Errorf("expected 0, got %d", count)
	}
}

func TestRouteStore_PruneExpiredRemovesExpiredAndReturnsCount(t *testing.T) {
	s := NewInMemoryRouteStore()
	_ = s.Save(rctx, expiredRoute("stale-1"))
	_ = s.Save(rctx, expiredRoute("stale-2"))
	_ = s.Save(rctx, freshRoute("fresh", "fresh"))
	count, err := s.PruneExpired(rctx)
	if err != nil {
		t.Fatalf("PruneExpired: %v", err)
	}
	if count != 2 {
		t.Errorf("expected 2 pruned, got %d", count)
	}
	got1, _ := s.Get(rctx, "stale-1")
	got2, _ := s.Get(rctx, "stale-2")
	if got1 != nil || got2 != nil {
		t.Error("expected stale routes to be deleted")
	}
	gotFresh, _ := s.Get(rctx, "fresh")
	if gotFresh == nil {
		t.Error("fresh route must survive pruning")
	}
}

func TestRouteStore_PruneExpiredReturnsZeroOnEmptyStore(t *testing.T) {
	s := NewInMemoryRouteStore()
	count, _ := s.PruneExpired(rctx)
	if count != 0 {
		t.Errorf("expected 0 on empty store, got %d", count)
	}
}

func TestRouteStore_PruneExpiredDoesNotTouchFreshRoutes(t *testing.T) {
	s := NewInMemoryRouteStore()
	_ = s.Save(rctx, freshRoute("node-f", "node-f"))
	_, _ = s.PruneExpired(rctx)
	got, _ := s.Get(rctx, "node-f")
	if got == nil {
		t.Error("fresh route should not be pruned")
	}
}
