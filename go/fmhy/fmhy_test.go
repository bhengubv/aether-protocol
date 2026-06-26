// SPDX-License-Identifier: MIT
package fmhy

import (
	"testing"
	"time"
)

const sampleMarkdown = `# Video
## Streaming
* **[FreeFlix](https://freeflix.example)** - Free movies and shows
* ⭐ **[BestStream](https://best.example)** - The top pick

# Audio
* **[TunePort](https://tune.example)** - Music streaming
`

func TestFmhyParseAndCatalogue(t *testing.T) {
	// Parser: headings -> category, bold link -> entry, ⭐ -> starred.
	parsed := ParseMarkdown(sampleMarkdown)
	if len(parsed) != 3 {
		t.Fatalf("parsed %d entries, want 3", len(parsed))
	}
	if parsed[0].Category != "Video / Streaming" {
		t.Fatalf("entry 0 category %q != 'Video / Streaming'", parsed[0].Category)
	}
	if parsed[0].Name != "FreeFlix" || parsed[0].URL != "https://freeflix.example" {
		t.Fatalf("entry 0 name/url wrong: %q %q", parsed[0].Name, parsed[0].URL)
	}
	if !parsed[1].IsStarred || parsed[1].Name != "BestStream" {
		t.Fatal("entry 1 should be the starred BestStream")
	}
	if parsed[2].Category != "Audio" {
		t.Fatalf("entry 2 category %q != 'Audio'", parsed[2].Category)
	}

	// Catalogue: sync replaces entries + fires OnSynced; browse/getStarred filter.
	svc := NewInMemoryCatalogueService(nil)
	if svc.EntryCount() != 0 {
		t.Fatalf("seed-less catalogue count %d != 0", svc.EntryCount())
	}
	synced := 0
	svc.OnSynced = func(total, added int, at time.Time) { synced++ }
	svc.Sync(sampleMarkdown)
	if svc.EntryCount() != 3 {
		t.Fatalf("post-sync count %d != 3", svc.EntryCount())
	}
	if synced != 1 {
		t.Fatalf("OnSynced fired %d times, want 1", synced)
	}

	if len(svc.Browse("")) != 3 {
		t.Fatalf("browse all returned %d, want 3", len(svc.Browse("")))
	}
	if len(svc.Browse("video")) != 2 {
		t.Fatalf("browse 'video' returned %d, want 2", len(svc.Browse("video")))
	}
	if len(svc.Browse("audio")) != 1 {
		t.Fatalf("browse 'audio' returned %d, want 1", len(svc.Browse("audio")))
	}
	if len(svc.Browse("nonexistent")) != 0 {
		t.Fatalf("browse 'nonexistent' returned %d, want 0", len(svc.Browse("nonexistent")))
	}

	starred := svc.GetStarred("")
	if len(starred) != 1 || starred[0].Name != "BestStream" {
		t.Fatalf("getStarred returned %d entries (want 1: BestStream)", len(starred))
	}
}
