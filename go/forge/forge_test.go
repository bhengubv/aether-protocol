// SPDX-License-Identifier: MIT
package forge

import (
	"context"
	"testing"
)

func TestForgeCacheQueryFetchStats(t *testing.T) {
	ctx := context.Background()
	svc := NewInMemoryService()

	fired := 0
	svc.OnNewEntryAnnounced = func(e *Entry) { fired++ }

	e, err := svc.Cache(ctx, "npm:react@18.2.0", "hash1", 1000)
	if err != nil {
		t.Fatal(err)
	}
	if e.DownloadCount != 0 {
		t.Fatalf("new entry download count %d != 0", e.DownloadCount)
	}
	if fired != 1 {
		t.Fatalf("OnNewEntryAnnounced fired %d times, want 1", fired)
	}

	// Idempotent re-cache: first write wins, no second announcement.
	e2, _ := svc.Cache(ctx, "npm:react@18.2.0", "hash2", 9999)
	if e2.ContentHash != "hash1" {
		t.Fatalf("re-cache not idempotent: content hash %q", e2.ContentHash)
	}
	if fired != 1 {
		t.Fatalf("re-cache fired the announcement again: %d", fired)
	}

	// Query hit + miss.
	if q, _ := svc.Query(ctx, "npm:react@18.2.0"); q == nil || q.ContentHash != "hash1" {
		t.Fatal("query miss for a cached package")
	}
	if miss, _ := svc.Query(ctx, "missing"); miss != nil {
		t.Fatal("query for an absent package should be nil")
	}

	// Fetch increments the download counter; miss returns nil.
	if f1, _ := svc.Fetch(ctx, "npm:react@18.2.0"); f1 == nil || f1.DownloadCount != 1 {
		t.Fatalf("first fetch did not increment download count")
	}
	_, _ = svc.Fetch(ctx, "npm:react@18.2.0")
	if fmiss, _ := svc.Fetch(ctx, "missing"); fmiss != nil {
		t.Fatal("fetch for an absent package should be nil")
	}

	// Stats: bytes-saved = downloads * size; one entry catalogued.
	st, _ := svc.GetStats(ctx)
	if st.CatalogueSize != 1 {
		t.Fatalf("catalogue size %d != 1", st.CatalogueSize)
	}
	if st.TotalBytesSaved != 2000 { // 2 downloads * 1000 bytes
		t.Fatalf("total bytes saved %d != 2000", st.TotalBytesSaved)
	}
}
