// SPDX-License-Identifier: MIT
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

// ── forgeCache unit tests ─────────────────────────────────────────────────────

func TestForgeCache_StoreAndLookup(t *testing.T) {
	c := newForgeCache()
	data := []byte("hello aether-forge")
	entry := c.Store("http://example.com/pkg.tgz", "npm:pkg@1.0.0", data)

	if entry == nil {
		t.Fatal("Store returned nil entry")
	}
	// Verify content hash is correct SHA-256.
	sum := sha256.Sum256(data)
	wantHash := hex.EncodeToString(sum[:])
	if entry.ContentHash != wantHash {
		t.Errorf("ContentHash = %s; want %s", entry.ContentHash, wantHash)
	}
	if entry.SizeBytes != int64(len(data)) {
		t.Errorf("SizeBytes = %d; want %d", entry.SizeBytes, len(data))
	}

	// Lookup should return the same bytes.
	blob, got, ok := c.Lookup("http://example.com/pkg.tgz")
	if !ok {
		t.Fatal("Lookup returned ok=false after Store")
	}
	if string(blob) != string(data) {
		t.Errorf("blob mismatch: got %q want %q", blob, data)
	}
	if got.ContentHash != wantHash {
		t.Errorf("entry hash mismatch")
	}
}

func TestForgeCache_MissIncrement(t *testing.T) {
	c := newForgeCache()
	_, _, ok := c.Lookup("http://not-cached.com/x")
	if ok {
		t.Fatal("expected cache miss, got hit")
	}
	stats := c.Stats(time.Unix(0, 0))
	if stats.TotalMisses != 1 {
		t.Errorf("TotalMisses = %d; want 1", stats.TotalMisses)
	}
}

func TestForgeCache_HitCountsAndBytesSaved(t *testing.T) {
	c := newForgeCache()
	data := []byte("bytes to save")
	c.Store("http://example.com/a", "pkg", data)

	for i := 0; i < 3; i++ {
		_, _, ok := c.Lookup("http://example.com/a")
		if !ok {
			t.Fatalf("Lookup returned false on iteration %d", i)
		}
	}

	stats := c.Stats(time.Unix(0, 0))
	if stats.TotalHits != 3 {
		t.Errorf("TotalHits = %d; want 3", stats.TotalHits)
	}
	wantSaved := int64(len(data)) * 3
	if stats.TotalBytesSaved != wantSaved {
		t.Errorf("TotalBytesSaved = %d; want %d", stats.TotalBytesSaved, wantSaved)
	}
}

func TestForgeCache_DeduplicatesOnHash(t *testing.T) {
	c := newForgeCache()
	data := []byte("same content")
	c.Store("http://cdn1.com/pkg.tgz", "npm:pkg@1", data)
	c.Store("http://cdn2.com/pkg.tgz", "npm:pkg@1", data) // identical bytes

	stats := c.Stats(time.Unix(0, 0))
	// Both URLs map to the same hash → only 1 entry in the catalogue.
	if stats.CatalogueSize != 1 {
		t.Errorf("CatalogueSize = %d; want 1 (dedup)", stats.CatalogueSize)
	}
}

// ── Proxy integration tests ───────────────────────────────────────────────────

func TestProxy_CacheMissThenHit(t *testing.T) {
	// Set up a fake origin server.
	origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte("origin-response"))
	}))
	defer origin.Close()

	proxy := newForgeProxy(false)
	proxyServer := httptest.NewServer(proxy)
	defer proxyServer.Close()

	proxyURL, _ := url.Parse(proxyServer.URL)
	client := &http.Client{
		Transport: &http.Transport{
			Proxy: http.ProxyURL(proxyURL),
		},
	}

	targetURL := origin.URL + "/resource"

	// First request — should be a cache MISS.
	resp1, err := client.Get(targetURL)
	if err != nil {
		t.Fatalf("first GET failed: %v", err)
	}
	body1, _ := io.ReadAll(resp1.Body)
	resp1.Body.Close()

	if string(body1) != "origin-response" {
		t.Errorf("body1 = %q; want %q", body1, "origin-response")
	}
	if resp1.Header.Get("X-Forge-Cache") != "MISS" {
		t.Errorf("X-Forge-Cache = %q; want MISS (first request)", resp1.Header.Get("X-Forge-Cache"))
	}

	// Second request — should be a cache HIT.
	resp2, err := client.Get(targetURL)
	if err != nil {
		t.Fatalf("second GET failed: %v", err)
	}
	body2, _ := io.ReadAll(resp2.Body)
	resp2.Body.Close()

	if string(body2) != "origin-response" {
		t.Errorf("body2 = %q; want %q", body2, "origin-response")
	}
	if resp2.Header.Get("X-Forge-Cache") != "HIT" {
		t.Errorf("X-Forge-Cache = %q; want HIT (second request)", resp2.Header.Get("X-Forge-Cache"))
	}

	// Verify content hash is present and correct.
	gotHash := resp2.Header.Get("X-Forge-Hash")
	sum := sha256.Sum256([]byte("origin-response"))
	wantHash := hex.EncodeToString(sum[:])
	if gotHash != wantHash {
		t.Errorf("X-Forge-Hash = %s; want %s", gotHash, wantHash)
	}
}

func TestProxy_StatsEndpoint(t *testing.T) {
	proxy := newForgeProxy(false)
	proxyServer := httptest.NewServer(proxy)
	defer proxyServer.Close()

	// Stats are served directly (no proxy needed — the path is local).
	resp, err := http.Get(proxyServer.URL + "/__forge/stats")
	if err != nil {
		t.Fatalf("GET /__forge/stats failed: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Errorf("status = %d; want 200", resp.StatusCode)
	}
	ct := resp.Header.Get("Content-Type")
	if !strings.HasPrefix(ct, "application/json") {
		t.Errorf("Content-Type = %q; want application/json", ct)
	}

	var stats ForgeStats
	if err := json.NewDecoder(resp.Body).Decode(&stats); err != nil {
		t.Fatalf("decode stats JSON: %v", err)
	}
	// Fresh proxy should have zero hits.
	if stats.TotalHits != 0 {
		t.Errorf("TotalHits = %d; want 0", stats.TotalHits)
	}
}

func TestProxy_NonIdempotentMethodNotCached(t *testing.T) {
	var callCount int
	origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		callCount++
		_, _ = w.Write([]byte("post-response"))
	}))
	defer origin.Close()

	proxy := newForgeProxy(false)
	proxyServer := httptest.NewServer(proxy)
	defer proxyServer.Close()

	proxyURL, _ := url.Parse(proxyServer.URL)
	client := &http.Client{
		Transport: &http.Transport{
			Proxy: http.ProxyURL(proxyURL),
		},
	}

	// POST the same URL twice.
	for i := 0; i < 2; i++ {
		resp, err := client.Post(origin.URL+"/api", "text/plain", strings.NewReader("data"))
		if err != nil {
			t.Fatalf("POST %d failed: %v", i, err)
		}
		resp.Body.Close()
	}

	// Both requests should have reached the origin (POST must not be cached).
	if callCount != 2 {
		t.Errorf("origin received %d calls; want 2 (POST must not be cached)", callCount)
	}
}

func TestProxy_TrackersEndpoint(t *testing.T) {
	proxy := newForgeProxy(false)
	proxyServer := httptest.NewServer(proxy)
	defer proxyServer.Close()

	resp, err := http.Get(proxyServer.URL + "/__forge/trackers")
	if err != nil {
		t.Fatalf("GET /__forge/trackers failed: %v", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		t.Errorf("status = %d; want 200", resp.StatusCode)
	}
	ct := resp.Header.Get("Content-Type")
	if !strings.HasPrefix(ct, "application/json") {
		t.Errorf("Content-Type = %q; want application/json", ct)
	}

	var sources []TrackerSource
	if err := json.NewDecoder(resp.Body).Decode(&sources); err != nil {
		t.Fatalf("decode trackers JSON: %v", err)
	}
	if len(sources) == 0 {
		t.Error("expected at least one tracker source; got none")
	}
	// Every source must have a non-empty Name and URL.
	for i, s := range sources {
		if s.Name == "" {
			t.Errorf("sources[%d].Name is empty", i)
		}
		if s.URL == "" {
			t.Errorf("sources[%d].URL is empty", i)
		}
	}
}

func TestPackageIDFromURL(t *testing.T) {
	cases := []struct {
		input string
		want  string
	}{
		{
			"https://registry.npmjs.org/react/-/react-18.2.0.tgz",
			"registry.npmjs.org/react/-/react-18.2.0.tgz",
		},
		{
			"http://files.pythonhosted.org/requests-2.31.0.tar.gz?sig=abc",
			"files.pythonhosted.org/requests-2.31.0.tar.gz",
		},
		{
			"https://github.com/user/repo/archive/abc123.tar.gz#checksum",
			"github.com/user/repo/archive/abc123.tar.gz",
		},
	}
	for _, tc := range cases {
		got := packageIDFromURL(tc.input)
		if got != tc.want {
			t.Errorf("packageIDFromURL(%q)\n  got  %q\n  want %q", tc.input, got, tc.want)
		}
	}
}
