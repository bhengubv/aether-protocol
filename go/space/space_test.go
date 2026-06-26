// SPDX-License-Identifier: MIT
package space

import (
	"context"
	"testing"
)

func TestSpaceDropScanDeletePrune(t *testing.T) {
	ctx := context.Background()
	svc := NewInMemoryService()

	received := 0
	svc.OnBreadcrumbReceived = func(b *Breadcrumb) { received++ }

	// Drop clamps a normal TTL into range; the received callback fires.
	a, err := svc.Drop(ctx, "k3vf9z", "hashA", "anchor1", BreadcrumbTypeNotice, 24)
	if err != nil {
		t.Fatal(err)
	}
	if a.TtlHours != 24 {
		t.Fatalf("notice ttl %d != 24", a.TtlHours)
	}
	if received != 1 {
		t.Fatalf("OnBreadcrumbReceived fired %d times, want 1", received)
	}

	// Emergency breadcrumbs get the fixed 720h TTL regardless of the request.
	e, _ := svc.Drop(ctx, "k3vf9z", "hashE", "anchor1", BreadcrumbTypeEmergency, 1)
	if e.TtlHours != EmergencyTtlHours {
		t.Fatalf("emergency ttl %d != %d", e.TtlHours, EmergencyTtlHours)
	}

	// Scan: geohash-prefix proximity hit vs a far cell.
	if near, _ := svc.Scan(ctx, "k3vf9z", 1); len(near) != 2 {
		t.Fatalf("scan near returned %d, want 2", len(near))
	}
	if far, _ := svc.Scan(ctx, "xxxxxx", 1); len(far) != 0 {
		t.Fatalf("scan far returned %d, want 0", len(far))
	}

	// Creator-only delete: a non-anchor requestor is refused; the anchor succeeds.
	if ok, _ := svc.Delete(ctx, a, "wrong"); ok {
		t.Fatal("non-anchor delete should fail")
	}
	if ok, _ := svc.Delete(ctx, a, "anchor1"); !ok {
		t.Fatal("anchor delete should succeed")
	}
	if after, _ := svc.Scan(ctx, "k3vf9z", 1); len(after) != 1 {
		t.Fatalf("after delete scan returned %d, want 1", len(after))
	}

	// Nothing is past its TTL yet.
	if n := svc.PruneExpired(); n != 0 {
		t.Fatalf("prune removed %d, want 0", n)
	}
}
