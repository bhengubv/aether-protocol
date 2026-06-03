// SPDX-License-Identifier: MIT
//
// aether-forge-proxy — Content-addressed HTTP caching proxy (reference implementation).
//
// Acts as an HTTP/1.1 forward proxy on localhost:2301 (configurable).
// Artifacts fetched through the proxy are hash-addressed with SHA-256 and
// cached in memory. Subsequent requests for the same URL are served from the
// local cache, saving upstream bandwidth and eliminating internet dependency
// for previously fetched content.
//
// This is the reference Go implementation of the aether-forge Phase 2
// extension.  Production deployments replace the in-memory blob store with
// an Aether IContentService peer-to-peer mesh store.
//
// Usage:
//
//	aether-forge-proxy [-addr :2301] [-verbose]
//
// Configure toolchains to use it:
//
//	git config --global http.proxy  http://localhost:2301
//	npm config set proxy            http://localhost:2301
//	pip config set global.proxy     http://localhost:2301
//
// Stats (JSON):
//
//	curl http://localhost:2301/__forge/stats
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

// ── Data model ─────────────────────────────────────────────────────────────────

// ForgeEntry describes a cached artifact.
type ForgeEntry struct {
	PackageID   string    `json:"package_id"`
	ContentHash string    `json:"content_hash"`
	FetchedAt   time.Time `json:"fetched_at"`
	SizeBytes   int64     `json:"size_bytes"`
	downloads   int64     // atomic; not exported in JSON (use Downloads() instead)
}

// Downloads returns the number of times this entry was served from cache.
func (e *ForgeEntry) Downloads() int64 { return atomic.LoadInt64(&e.downloads) }

// MarshalJSON serialises the entry including the atomic download counter.
func (e *ForgeEntry) MarshalJSON() ([]byte, error) {
	type alias struct {
		PackageID     string    `json:"package_id"`
		ContentHash   string    `json:"content_hash"`
		FetchedAt     time.Time `json:"fetched_at"`
		SizeBytes     int64     `json:"size_bytes"`
		DownloadCount int64     `json:"download_count"`
	}
	return json.Marshal(alias{
		PackageID:     e.PackageID,
		ContentHash:   e.ContentHash,
		FetchedAt:     e.FetchedAt,
		SizeBytes:     e.SizeBytes,
		DownloadCount: e.Downloads(),
	})
}

// ForgeStats carries aggregated proxy usage metrics.
type ForgeStats struct {
	TotalBytesSaved int64        `json:"total_bytes_saved"`
	TotalHits       int64        `json:"total_hits"`
	TotalMisses     int64        `json:"total_misses"`
	CatalogueSize   int          `json:"catalogue_size"`
	UptimeSeconds   int64        `json:"uptime_seconds"`
	TopEntries      []*ForgeEntry `json:"top_entries"`
}

// ── Content-addressed cache ────────────────────────────────────────────────────

// forgeCache stores content-addressed artifacts: SHA-256 hex → bytes.
// All public methods are safe for concurrent use.
type forgeCache struct {
	mu      sync.RWMutex
	entries map[string]*ForgeEntry // SHA-256 hex → metadata
	blobs   map[string][]byte      // SHA-256 hex → raw bytes
	urlIdx  map[string]string      // normalised URL → SHA-256 hex

	// Aggregate counters (updated atomically).
	totalHits       int64
	totalMisses     int64
	totalBytesSaved int64
}

func newForgeCache() *forgeCache {
	return &forgeCache{
		entries: make(map[string]*ForgeEntry),
		blobs:   make(map[string][]byte),
		urlIdx:  make(map[string]string),
	}
}

// Lookup returns the cached blob and metadata for url, or (nil, nil, false).
func (c *forgeCache) Lookup(url string) ([]byte, *ForgeEntry, bool) {
	c.mu.RLock()
	hash, ok := c.urlIdx[url]
	if !ok {
		c.mu.RUnlock()
		atomic.AddInt64(&c.totalMisses, 1)
		return nil, nil, false
	}
	entry := c.entries[hash]
	blob := c.blobs[hash]
	c.mu.RUnlock()

	if entry == nil || blob == nil {
		atomic.AddInt64(&c.totalMisses, 1)
		return nil, nil, false
	}

	atomic.AddInt64(&entry.downloads, 1)
	atomic.AddInt64(&c.totalHits, 1)
	atomic.AddInt64(&c.totalBytesSaved, entry.SizeBytes)
	return blob, entry, true
}

// Store hashes data and saves it under url with the given packageID.
// Returns the new ForgeEntry (or the existing entry if url was already cached).
func (c *forgeCache) Store(url, packageID string, data []byte) *ForgeEntry {
	sum := sha256.Sum256(data)
	hash := hex.EncodeToString(sum[:])

	c.mu.Lock()
	defer c.mu.Unlock()

	if existing, ok := c.entries[hash]; ok {
		// Content already cached under this hash — just add the URL alias.
		c.urlIdx[url] = hash
		return existing
	}

	entry := &ForgeEntry{
		PackageID:   packageID,
		ContentHash: hash,
		FetchedAt:   time.Now().UTC(),
		SizeBytes:   int64(len(data)),
	}
	c.entries[hash] = entry
	c.blobs[hash] = data
	c.urlIdx[url] = hash
	return entry
}

// Stats returns current aggregate statistics.
func (c *forgeCache) Stats(startedAt time.Time) ForgeStats {
	c.mu.RLock()
	size := len(c.entries)

	// Pick up to 5 most-downloaded entries.
	top := make([]*ForgeEntry, 0, len(c.entries))
	for _, e := range c.entries {
		top = append(top, e)
	}
	c.mu.RUnlock()

	// Sort by download count descending (simple selection; catalogue is small).
	for i := 0; i < len(top) && i < 5; i++ {
		maxIdx := i
		for j := i + 1; j < len(top); j++ {
			if top[j].Downloads() > top[maxIdx].Downloads() {
				maxIdx = j
			}
		}
		top[i], top[maxIdx] = top[maxIdx], top[i]
	}
	if len(top) > 5 {
		top = top[:5]
	}

	return ForgeStats{
		TotalBytesSaved: atomic.LoadInt64(&c.totalBytesSaved),
		TotalHits:       atomic.LoadInt64(&c.totalHits),
		TotalMisses:     atomic.LoadInt64(&c.totalMisses),
		CatalogueSize:   size,
		UptimeSeconds:   int64(time.Since(startedAt).Seconds()),
		TopEntries:      top,
	}
}

// ── Proxy server ───────────────────────────────────────────────────────────────

// forgeProxy is the HTTP forward proxy.
type forgeProxy struct {
	cache     *forgeCache
	verbose   bool
	startedAt time.Time
	// Reusable HTTP client for outbound fetches (no proxy — direct internet).
	client *http.Client
}

func newForgeProxy(verbose bool) *forgeProxy {
	return &forgeProxy{
		cache:     newForgeCache(),
		verbose:   verbose,
		startedAt: time.Now(),
		client: &http.Client{
			Timeout: 60 * time.Second,
			// Use a transport with no proxy so outbound requests go direct.
			Transport: &http.Transport{
				Proxy:                 nil,
				ForceAttemptHTTP2:     false,
				MaxIdleConns:          100,
				IdleConnTimeout:       90 * time.Second,
				TLSHandshakeTimeout:   10 * time.Second,
				ExpectContinueTimeout: 1 * time.Second,
			},
		},
	}
}

// ServeHTTP dispatches to the appropriate handler.
func (p *forgeProxy) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	switch {
	case r.Method == http.MethodConnect:
		p.handleConnect(w, r)
	case r.URL.Path == "/__forge/stats":
		p.handleStats(w, r)
	default:
		p.handleHTTP(w, r)
	}
}

// handleConnect tunnels an HTTP CONNECT request directly to the destination.
// HTTPS traffic is forwarded without inspection (no MITM).
func (p *forgeProxy) handleConnect(w http.ResponseWriter, r *http.Request) {
	dest := r.Host
	if p.verbose {
		log.Printf("CONNECT tunnel → %s", dest)
	}

	upstream, err := net.DialTimeout("tcp", dest, 15*time.Second)
	if err != nil {
		http.Error(w, fmt.Sprintf("could not connect to %s: %v", dest, err), http.StatusBadGateway)
		return
	}
	defer upstream.Close()

	hijacker, ok := w.(http.Hijacker)
	if !ok {
		http.Error(w, "hijacking not supported", http.StatusInternalServerError)
		return
	}
	clientConn, _, err := hijacker.Hijack()
	if err != nil {
		http.Error(w, err.Error(), http.StatusServiceUnavailable)
		return
	}
	defer clientConn.Close()

	// Acknowledge the CONNECT.
	_, _ = clientConn.Write([]byte("HTTP/1.1 200 Connection Established\r\n\r\n"))

	// Bidirectional pipe between client and upstream.
	done := make(chan struct{}, 2)
	pipe := func(dst, src net.Conn) {
		_, _ = io.Copy(dst, src)
		done <- struct{}{}
	}
	go pipe(upstream, clientConn)
	go pipe(clientConn, upstream)
	<-done
}

// handleHTTP serves cacheable HTTP requests (GET/HEAD).
// On cache miss the request is forwarded, the response is cached, and the
// cached body is returned to the client.
func (p *forgeProxy) handleHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		// Non-idempotent methods (POST, PUT, DELETE, …) are forwarded without
		// caching — they modify server state and caching would be incorrect.
		p.forward(w, r)
		return
	}

	cacheKey := r.URL.String()

	// ── Cache hit ────────────────────────────────────────────────────────────
	if blob, entry, ok := p.cache.Lookup(cacheKey); ok {
		if p.verbose {
			log.Printf("HIT  %s (%d bytes, hash=%s)", cacheKey, entry.SizeBytes, entry.ContentHash[:12])
		}
		w.Header().Set("X-Forge-Cache", "HIT")
		w.Header().Set("X-Forge-Hash", entry.ContentHash)
		w.Header().Set("Content-Length", fmt.Sprintf("%d", entry.SizeBytes))
		if r.Method == http.MethodGet {
			_, _ = w.Write(blob)
		}
		return
	}

	// ── Cache miss — fetch from origin ────────────────────────────────────────
	outReq, err := http.NewRequestWithContext(r.Context(), r.Method, r.URL.String(), r.Body)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	// Copy safe request headers.
	for k, vs := range r.Header {
		for _, v := range vs {
			outReq.Header.Add(k, v)
		}
	}
	outReq.Header.Del("Proxy-Connection")

	resp, err := p.client.Do(outReq)
	if err != nil {
		if p.verbose {
			log.Printf("MISS %s — fetch error: %v", cacheKey, err)
		}
		http.Error(w, fmt.Sprintf("upstream fetch failed: %v", err), http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		http.Error(w, fmt.Sprintf("reading response: %v", err), http.StatusBadGateway)
		return
	}

	// Cache successful 2xx responses.
	if resp.StatusCode >= 200 && resp.StatusCode < 300 && r.Method == http.MethodGet {
		packageID := packageIDFromURL(r.URL.String())
		entry := p.cache.Store(cacheKey, packageID, body)
		if p.verbose {
			log.Printf("MISS %s → cached %d bytes (hash=%s)", cacheKey, entry.SizeBytes, entry.ContentHash[:12])
		}
		w.Header().Set("X-Forge-Cache", "MISS")
		w.Header().Set("X-Forge-Hash", entry.ContentHash)
	}

	// Forward response headers and status.
	for k, vs := range resp.Header {
		for _, v := range vs {
			w.Header().Add(k, v)
		}
	}
	w.WriteHeader(resp.StatusCode)
	_, _ = w.Write(body)
}

// forward proxies a request without any caching (used for non-idempotent methods).
func (p *forgeProxy) forward(w http.ResponseWriter, r *http.Request) {
	outReq, err := http.NewRequestWithContext(r.Context(), r.Method, r.URL.String(), r.Body)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	for k, vs := range r.Header {
		for _, v := range vs {
			outReq.Header.Add(k, v)
		}
	}
	outReq.Header.Del("Proxy-Connection")

	resp, err := p.client.Do(outReq)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()

	for k, vs := range resp.Header {
		for _, v := range vs {
			w.Header().Add(k, v)
		}
	}
	w.WriteHeader(resp.StatusCode)
	_, _ = io.Copy(w, resp.Body)
}

// handleStats returns aggregate statistics as JSON.
func (p *forgeProxy) handleStats(w http.ResponseWriter, _ *http.Request) {
	stats := p.cache.Stats(p.startedAt)
	w.Header().Set("Content-Type", "application/json")
	enc := json.NewEncoder(w)
	enc.SetIndent("", "  ")
	_ = enc.Encode(stats)
}

// packageIDFromURL derives a human-readable package identifier from a URL.
// This heuristic covers the most common package manager URL patterns.
func packageIDFromURL(rawURL string) string {
	// Trim query string and fragment for a cleaner ID.
	for _, sep := range []byte{'?', '#'} {
		for i := 0; i < len(rawURL); i++ {
			if rawURL[i] == sep {
				rawURL = rawURL[:i]
				break
			}
		}
	}
	// Return the URL path as the package identifier (host + path, no scheme).
	for _, prefix := range []string{"https://", "http://"} {
		if len(rawURL) > len(prefix) && rawURL[:len(prefix)] == prefix {
			rawURL = rawURL[len(prefix):]
			break
		}
	}
	return rawURL
}

// ── Entry point ────────────────────────────────────────────────────────────────

func main() {
	addr := flag.String("addr", "localhost:2301", "proxy listen address")
	verbose := flag.Bool("verbose", false, "log every cache hit/miss")
	flag.Parse()

	proxy := newForgeProxy(*verbose)

	server := &http.Server{
		Addr:         *addr,
		Handler:      proxy,
		ReadTimeout:  30 * time.Second,
		WriteTimeout: 60 * time.Second,
		IdleTimeout:  120 * time.Second,
	}

	// Graceful shutdown on SIGINT / SIGTERM.
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	go func() {
		sig := <-sigCh
		log.Printf("aether-forge-proxy: received %v, shutting down…", sig)
		stats := proxy.cache.Stats(proxy.startedAt)
		log.Printf("Final stats: %d hits, %d misses, %d bytes saved, %d entries cached",
			stats.TotalHits, stats.TotalMisses, stats.TotalBytesSaved, stats.CatalogueSize)
		_ = server.Close()
	}()

	log.Printf("aether-forge-proxy listening on %s", *addr)
	log.Printf("Configure your tools:")
	log.Printf("  git config --global http.proxy  http://%s", *addr)
	log.Printf("  npm config set proxy            http://%s", *addr)
	log.Printf("  pip config set global.proxy     http://%s", *addr)
	log.Printf("Stats: curl http://%s/__forge/stats", *addr)

	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		fmt.Fprintf(os.Stderr, "error: %v\n", err)
		os.Exit(1)
	}
}
