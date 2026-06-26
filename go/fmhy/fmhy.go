// SPDX-License-Identifier: MIT

// Package fmhy implements the Free Media Heck Yeah (FMHY) content catalogue
// (Phase-2 extension), propagated over the Aether mesh so offline peers benefit
// from entries fetched by connected peers. Port of the C# reference
// (AetherNet.Fmhy): a markdown parser for the FMHY single-page dump plus an
// in-memory catalogue with browse / starred / tracker-source access.
package fmhy

import (
	"regexp"
	"strings"
	"time"
)

// Entry is a single resource parsed from the FMHY directory.
type Entry struct {
	Name        string
	URL         string
	Description  string // empty if none
	Category    string // "H1" or "H1 / H2"
	IsStarred   bool   // carries the FMHY ⭐ star
	Mirrors     []string
}

// AllURLs returns the primary URL followed by any mirrors.
func (e *Entry) AllURLs() []string {
	if len(e.Mirrors) == 0 {
		return []string{e.URL}
	}
	return append([]string{e.URL}, e.Mirrors...)
}

// TrackerSource is a known torrent tracker-list aggregator.
type TrackerSource struct {
	Name        string
	URL         string
	Description string
}

// BuiltInTrackerSources are the well-known public tracker-list aggregators
// bundled with this release.
var BuiltInTrackerSources = []TrackerSource{
	{"ngosang/trackerslist", "https://ngosang.github.io/trackerslist/trackers_all.txt", "Community-maintained list of all known public BitTorrent trackers."},
	{"XIU2/TrackersListCollection (all)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/all.txt", "Comprehensive tracker collection maintained by XIU2, updated daily."},
	{"XIU2/TrackersListCollection (best)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt", "Curated best-performing tracker subset from the XIU2 collection."},
	{"newtrackon (stable)", "https://newtrackon.com/api/stable", "Live-monitored stable tracker list from newtrackon.com."},
	{"openwebtorrent", "https://openwebtorrent.com/", "Free WebTorrent-compatible tracker for browser-based torrenting."},
}

// FmhyApiURL is the public FMHY single-page endpoint.
const FmhyApiURL = "https://api.fmhy.net/single-page"

var (
	boldLinkRe  = regexp.MustCompile(`\*\*\[([^\]]+)\]\(([^)]+)\)\*\*`)
	plainLinkRe = regexp.MustCompile(`\[([^\]]+)\]\(([^)]+)\)`)
	headingRe   = regexp.MustCompile(`^(#{1,2})\s+(.+)$`)
	bulletRe    = regexp.MustCompile(`^\s*[*\-]\s+(.+)$`)
)

// ParseMarkdown parses a raw FMHY markdown string into a flat list of entries in
// document order. Lines that do not match the expected bullet pattern are skipped.
func ParseMarkdown(markdown string) []Entry {
	entries := make([]Entry, 0, 256)
	h1, h2 := "", ""

	for _, raw := range strings.Split(markdown, "\n") {
		line := strings.TrimRight(raw, " \t\r")
		if len(line) == 0 {
			continue
		}

		if hm := headingRe.FindStringSubmatch(line); hm != nil {
			level := len(hm[1])
			title := strings.TrimSpace(hm[2])
			if level == 1 {
				h1, h2 = title, ""
			} else {
				h2 = title
			}
			continue
		}

		bm := bulletRe.FindStringSubmatch(line)
		if bm == nil {
			continue
		}
		content := bm[1]
		isStarred := strings.Contains(content, "⭐")

		boldLoc := boldLinkRe.FindStringSubmatchIndex(content)
		if boldLoc == nil {
			continue
		}
		name := strings.TrimSpace(content[boldLoc[2]:boldLoc[3]])
		url := strings.TrimSpace(content[boldLoc[4]:boldLoc[5]])
		if url == "" || strings.HasPrefix(url, "#") {
			continue
		}
		boldEnd := boldLoc[1]

		// Description: text after the first " - " following the bold link.
		desc := ""
		descSep := strings.Index(content[boldEnd:], " - ")
		if descSep >= 0 {
			descSep += boldEnd
			desc = strings.TrimSpace(content[descSep+3:])
			desc = strings.TrimSpace(plainLinkRe.ReplaceAllString(desc, "$1"))
		}

		// Mirrors: plain [Name](URL) links between the bold link and the description.
		mirrorRegion := content[boldEnd:]
		if descSep >= 0 {
			mirrorRegion = content[boldEnd:descSep]
		}
		var mirrors []string
		for _, pm := range plainLinkRe.FindAllStringSubmatch(mirrorRegion, -1) {
			mu := strings.TrimSpace(pm[2])
			if mu != "" && mu != url && !strings.HasPrefix(mu, "#") {
				mirrors = append(mirrors, mu)
			}
		}

		category := h1
		if h2 != "" {
			category = h1 + " / " + h2
		}
		entries = append(entries, Entry{
			Name: name, URL: url, Description: desc, Category: category,
			IsStarred: isStarred, Mirrors: mirrors,
		})
	}
	return entries
}

// CatalogueService provides access to the FMHY content catalogue.
type CatalogueService interface {
	// Sync replaces the catalogue from a fresh FMHY markdown string.
	Sync(markdown string)
	// Browse returns all entries, optionally filtered by a case-insensitive category substring.
	Browse(categoryFilter string) []Entry
	// GetStarred returns only starred entries, optionally category-filtered.
	GetStarred(categoryFilter string) []Entry
	// GetTrackerSources returns the bundled tracker-list aggregators.
	GetTrackerSources() []TrackerSource
	// EntryCount is the number of loaded entries.
	EntryCount() int
}

// InMemoryCatalogueService is an in-memory CatalogueService seeded optionally
// with entries and updated via Sync.
type InMemoryCatalogueService struct {
	entries      []Entry
	lastSyncedAt *time.Time

	// OnSynced fires when Sync installs new entries: (total, new, syncedAt).
	OnSynced func(total, added int, syncedAt time.Time)
}

// NewInMemoryCatalogueService constructs a catalogue from an optional seed list.
func NewInMemoryCatalogueService(seed []Entry) *InMemoryCatalogueService {
	return &InMemoryCatalogueService{entries: seed}
}

// LastSyncedAt returns the UTC time of the last Sync, or nil if seed-only.
func (s *InMemoryCatalogueService) LastSyncedAt() *time.Time { return s.lastSyncedAt }

// EntryCount implements CatalogueService.
func (s *InMemoryCatalogueService) EntryCount() int { return len(s.entries) }

// Sync implements CatalogueService.
func (s *InMemoryCatalogueService) Sync(markdown string) {
	before := len(s.entries)
	parsed := ParseMarkdown(markdown)
	now := time.Now().UTC()
	s.entries = parsed
	s.lastSyncedAt = &now
	if s.OnSynced != nil {
		s.OnSynced(len(parsed), len(parsed)-before, now)
	}
}

// Browse implements CatalogueService.
func (s *InMemoryCatalogueService) Browse(categoryFilter string) []Entry {
	if categoryFilter == "" {
		return s.entries
	}
	cf := strings.ToLower(categoryFilter)
	out := make([]Entry, 0)
	for _, e := range s.entries {
		if strings.Contains(strings.ToLower(e.Category), cf) {
			out = append(out, e)
		}
	}
	return out
}

// GetStarred implements CatalogueService.
func (s *InMemoryCatalogueService) GetStarred(categoryFilter string) []Entry {
	cf := strings.ToLower(categoryFilter)
	out := make([]Entry, 0)
	for _, e := range s.entries {
		if !e.IsStarred {
			continue
		}
		if categoryFilter != "" && !strings.Contains(strings.ToLower(e.Category), cf) {
			continue
		}
		out = append(out, e)
	}
	return out
}

// GetTrackerSources implements CatalogueService.
func (s *InMemoryCatalogueService) GetTrackerSources() []TrackerSource {
	return BuiltInTrackerSources
}
